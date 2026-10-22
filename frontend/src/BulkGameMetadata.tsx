import { useEffect, useRef, useState } from "react";
import { api } from "./api";

export interface GameTitleResult {
  titleId: string;
  title: string;
  source: string;
  thumbnailUrl?: string;
}

export function BulkGameMetadata({ games, busy, run, applied }: {
  games: { titleId: string; title: string }[];
  busy: boolean;
  run: (fn: () => Promise<void>) => Promise<void>;
  applied: (result: GameTitleResult) => void;
}) {
  const [language, setLanguage] = useState("ko");
  const [progress, setProgress] = useState<{ total: number; done: number; success: number; active: string; stopped: boolean } | null>(null);
  const [failures, setFailures] = useState<{ titleId: string; title: string; error: string }[]>([]);
  const [running, setRunning] = useState(false);
  const [stopping, setStopping] = useState(false);
  const stop = useRef(false);
  const inFlight = useRef(false);
  useEffect(() => () => { stop.current = true; }, []);
  const targets = [...new Map(games.map(game => [game.titleId.toUpperCase(), { ...game, titleId: game.titleId.toUpperCase() }])).values()];

  async function fetchAll() {
    if (inFlight.current || !targets.length) return;
    inFlight.current = true;
    stop.current = false;
    setRunning(true); setStopping(false); setFailures([]);
    let done = 0, success = 0;
    const report = (active = "") => setProgress({ total: targets.length, done, success, active, stopped: stop.current });
    report();
    try {
      for (const game of targets) {
        if (stop.current) break;
        report(game.title);
        try {
          const result = await api<GameTitleResult>("/game-titles/" + game.titleId + "/web", "POST", { language });
          applied(result);
          success++;
        } catch (error) {
          setFailures(current => [...current, { ...game, error: error instanceof Error ? error.message : "조회하지 못했습니다." }]);
        }
        done++;
        report();
      }
    } finally {
      report();
      setRunning(false);
      inFlight.current = false;
    }
  }

  return <section className="card bulk-game-metadata">
    <div className="card-head"><div><h2>게임 정보 한 번에 가져오기</h2>
      <p>현재 탐색한 게임과 모든 등록된 동기화 대상 {targets.length}개 타이틀을 조회합니다. 같은 Title ID는 한 번만 조회합니다.</p>
    </div></div>
    <div className="card-body">
      <p className="hint">버튼을 누르면 Title ID를 Nlib API에 보내 이름·썸네일을 차례로 가져옵니다. 직접 지정한 이름은 유지하며, 실패한 게임은 기존 정보를 유지하고 다음 게임을 계속 처리합니다.</p>
      <label className="field"><span>웹 게임 이름 언어</span>
        <select value={language} disabled={busy || running} onChange={e => setLanguage(e.target.value)}>
          <option value="ko">한국어</option><option value="en">English</option><option value="ja">日本語</option>
        </select>
      </label>
      <div className="row-actions">
        <button disabled={busy || running || !targets.length} onClick={() => run(fetchAll)}>전체 이름·썸네일 가져오기 ({targets.length})</button>
        {running && <button className="secondary" disabled={stopping} onClick={() => { stop.current = true; setStopping(true); }}>
          {stopping ? "현재 조회 후 중지 중…" : "현재 조회 후 중지"}
        </button>}
      </div>
      {!targets.length && <p className="hint">먼저 아래에서 설치본을 선택하고 ‘다시 탐색’을 누르세요.</p>}
      {progress && <div role="status" aria-live="polite">
        <p>{running ? "조회 중" : progress.stopped && progress.done < progress.total ? "중지됨" : "조회 완료"}: {progress.done}/{progress.total} · 성공 {progress.success} · 실패 {progress.done - progress.success}
          {progress.active && ` · ${progress.active}`}</p>
        <progress aria-label="게임 정보 조회 진행률" max={progress.total} value={progress.done} />
        {!running && progress.done < progress.total && <p>미처리 {progress.total - progress.done}개. 완료된 정보는 저장되었습니다.</p>}
      </div>}
      {failures.length > 0 && <details><summary>조회 실패 {failures.length}개 보기</summary>
        <ul>{failures.map(f => <li key={f.titleId}>{f.title} ({f.titleId}): {f.error}</li>)}</ul>
      </details>}
      <p className="hint">다른 설치본의 미등록 게임도 가져오려면 해당 설치본을 탐색한 뒤 실행하세요. 이 화면을 벗어나면 현재 조회까지만 처리합니다.</p>
    </div>
  </section>;
}
