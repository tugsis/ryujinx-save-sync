import { useState } from "react";
import { api, type Settings, type Status, type Binding, type Job, type Outcome, type LaunchProfile, type Installation } from "./api";
import { CredentialSettings } from "./CredentialSettings";
import "./deck.css";

type Props = {
  settings: Settings; status: Status | null; bindings: Binding[]; jobs: Job[]; profiles: LaunchProfile[];
  busy: boolean; dirty: boolean; notice: string;
  change: <K extends keyof Settings>(key: K, value: Settings[K]) => void;
  save: () => Promise<void>; run: (fn: () => Promise<void>) => Promise<void>;
  sync: (id?: string) => Promise<void>; scan: (id: string, saveFirst?: boolean) => Promise<void>;
  notify: (text: string) => void; resolve: (outcome: Outcome) => void;
  detailed: (page: string) => void;
};
const resultLabels: Record<string, string> = { unchanged: "✓ 내용 일치", uploaded: "↑ 업로드 완료", downloaded: "↓ 내려받기 완료", conflict: "버전 선택 필요", "compatibility-required": "호환성 확인 필요", failed: "실패 · 확인 필요", empty: "세이브 내용 확인 필요" };
export function DeckView(p: Props) {
  const [page, setPage] = useState("sync");
  const [section, setSection] = useState("server");
  const [installationId, setInstallationId] = useState("legacy");
  const [advanced, setAdvanced] = useState(false);
  const activeJob = p.jobs.find(j => ["queued", "running", "waiting"].includes(j.status));
  const settingsDisabled = p.busy || !!p.status?.launch || !!activeJob;
  const installations: Installation[] = [{ id: "legacy", name: "기본 Ryujinx", kind: "ryujinx", build: "", dataRoot: p.settings.dataRoot, nandRoot: "", executable: p.settings.executable, workingDirectory: p.settings.workingDirectory, arguments: p.settings.arguments }, ...p.settings.installations];
  const installation = installations.find(i => i.id === installationId) ?? installations[0];
  function go(next: string) {
    if (page === "settings" && p.dirty && !window.confirm("저장하지 않은 변경이 있습니다. 변경 내용을 유지한 채 화면을 이동할까요?")) return;
    setPage(next);
  }
  function updateInstallation(patch: Partial<Installation>) {
    if (installation.id === "legacy") {
      if (patch.dataRoot !== undefined) p.change("dataRoot", patch.dataRoot);
      if (patch.executable !== undefined) p.change("executable", patch.executable);
      if (patch.workingDirectory !== undefined) p.change("workingDirectory", patch.workingDirectory);
      if (patch.arguments !== undefined) p.change("arguments", patch.arguments);
    } else p.change("installations", p.settings.installations.map(i => i.id === installation.id ? { ...i, ...patch } : i));
  }
  const last = (id: string) => {
    for (const job of p.jobs) {
      const result = job.results.find(r => r.bindingId === id);
      if (result) return { result, at: job.createdAt };
    }
    return null;
  };
  const unresolved = p.bindings.filter(b => ["conflict", "compatibility-required"].includes(last(b.id)?.result.status ?? "")).length;
  const saved = async (test: boolean) => {
    await p.save();
    if (test) { await api("/storage/test", "POST", {}); p.notify("설정을 저장하고 WebDAV 연결을 확인했습니다."); }
  };
  return <div className="deck-app">
    <header className="deck-header">
      <div className="deck-brand"><span>↻</span> RyuSync <small>DECK</small></div>
      <nav aria-label="Deck 화면"><button aria-pressed={page === "sync"} onClick={() => go("sync")}>동기화</button><button aria-pressed={page === "settings"} onClick={() => go("settings")}>설정</button></nav>
      <div className="deck-device"><strong>{p.status?.device}</strong><small>{p.status?.version} · {p.settings.workspace}</small></div>
    </header>
    <main className="deck-main">
      {p.notice && <div className="deck-notice" role="status"><span>{p.notice}</span><button className="text-button" aria-label="알림 닫기" onClick={() => p.notify("")}>닫기</button></div>}
      {!!p.status?.recovery && <div className="warning">미완료 세이브 적용이 {p.status.recovery}건 있습니다. 상세 화면의 백업·복구 안내를 확인하세요.</div>}
      <div className="deck-heading"><div><small>{page === "sync" ? "READY FOR YOUR NEXT PLAY" : "SET UP ONCE, KEEP PLAYING"}</small><h1>{page === "sync" ? "이어서 플레이할 준비" : "이 기기 설정"}</h1><p>{page === "sync" ? "마지막 작업 결과를 확인하고 서버와 다시 동기화하세요." : "서버·비밀번호·에뮬레이터·Steam 실행을 설정하세요."}</p></div>{p.dirty && <span className="badge amber">저장하지 않은 변경</span>}</div>
      {page === "sync" ? <div className="deck-sync-layout">
        <section className="card"><div className="card-head"><h2>내 게임 <small>{p.bindings.length}</small></h2><button className="text-button" onClick={() => p.detailed("games")}>등록·서버 목록</button></div>
          {!p.bindings.length && <div className="card-body"><h3>아직 등록된 게임이 없어요</h3><p>설정에서 데이터 폴더를 지정하고 게임을 탐색하세요.</p><button onClick={() => { setSection("emulator"); setPage("settings"); }}>설치본 설정·게임 탐색</button></div>}
          {p.bindings.map(b => { const item = last(b.id); const conflict = item && ["conflict", "compatibility-required"].includes(item.result.status); return <article className="deck-game" key={b.id}>
            <div className="deck-cover">{b.thumbnailUrl ? <img alt="" src={b.thumbnailUrl} onError={e => { e.currentTarget.style.display = "none"; }} /> : "◇"}</div>
            <div className="deck-game-info"><h3>{b.title}</h3><small>{b.emulatorKind} · {b.slot} · {installations.find(i => i.id === b.installationId)?.name ?? b.installationId}</small>
              <p className={conflict ? "deck-alert" : ""}>{item ? resultLabels[item.result.status] ?? item.result.message : "아직 확인하지 않았어요"}</p>
              <small>{item ? "마지막 작업 · " + new Date(item.at).toLocaleString("ko-KR") : "동기화하면 서버와 비교합니다."}</small>
            </div>
            <button className="secondary" disabled={p.busy || !!activeJob || p.dirty || !b.enabled || !p.status?.credentialUnlocked || !!p.status?.recovery || (!!p.status?.launch && !!conflict)} onClick={() => conflict ? p.resolve(item.result) : p.run(() => p.sync(b.id))}>{conflict ? "버전 확인" : p.status?.launch ? "종료 후 예약" : "동기화"}</button>
          </article>; })}
        </section>
        <aside className="deck-summary"><section className="card deck-summary-card"><span className="deck-symbol">↕</span><h2>{p.status?.launch ? "게임 실행 세션 진행 중" : activeJob ? "동기화 작업 진행 중" : unresolved ? `${unresolved}개 게임에 확인이 필요해요` : "진행 상황을 맞춰볼까요?"}</h2><p>{p.status?.launch?.message ?? activeJob?.message ?? "서버와 이 기기를 다시 비교한 뒤 변경 사항을 적용합니다."}</p>
          {activeJob && <p role="status">{activeJob.status === "waiting" ? "게임 종료 후 대기" : `처리 결과 ${activeJob.results.length}개 · ${activeJob.status === "running" ? "실행 중" : "대기 중"}`}</p>}
          <button className="primary" disabled={p.busy || !!activeJob || p.dirty || !p.bindings.some(b => b.enabled) || !!p.status?.recovery || !p.status?.credentialUnlocked} onClick={() => p.run(() => p.sync())}>{p.status?.launch ? "게임 종료 후 전체 동기화 예약" : "전체 동기화"}</button>
          {p.dirty && <p>변경한 설정을 먼저 저장하세요.</p>}
          {!p.status?.credentialUnlocked && <button className="secondary" onClick={() => { setPage("settings"); setSection("password"); }}>비밀번호 설정</button>}
        </section><section className="card"><div className="card-body"><h3>실행 전 확인</h3><p className="deck-check">인증 <strong>{p.status?.credentialUnlocked ? "사용 준비됨" : "입력 필요"}</strong></p><p className="deck-check">실행 세션 <strong>{p.status?.launch ? "진행 중" : "없음"}</strong></p><small>외부 게임 실행과 새 서버 변경은<br />동기화할 때 다시 확인합니다.</small></div></section></aside>
      </div> : <div className="deck-settings-layout">
        <nav className="deck-settings-nav" aria-label="설정 분류">{[["server", "서버 연결", "WebDAV 주소와 계정"], ["password", "비밀번호", "파일 · OS 보관함 · 세션"], ["emulator", "에뮬레이터", "데이터 폴더와 게임 탐색"], ["steam", "Steam 실행", "게임 시작 전·후 동기화"]].map(([key, title, hint], index) => <button key={key} aria-pressed={section === key} onClick={() => setSection(key)}>{String(index + 1).padStart(2, "0")}　{title}<small>{hint}</small></button>)}</nav>
        <section className="card deck-settings-card"><div className="card-body">
          {section === "server" && <fieldset disabled={settingsDisabled}><h2>WebDAV 서버 연결</h2><label className="field">서버 주소 <small>동기화 시 필수</small><input type="url" value={p.settings.webDavUrl} placeholder="https://nas.example.com/webdav/" onChange={e => p.change("webDavUrl", e.target.value)} /></label><div className="form-row"><label className="field">사용자 이름 <small>서버 인증 시 필수</small><input value={p.settings.webDavUser} onChange={e => p.change("webDavUser", e.target.value)} /></label><label className="field">동기화 공간 <small>필수</small><input value={p.settings.workspace} onChange={e => p.change("workspace", e.target.value)} /></label></div><label className="field">기기 이름 <small>필수</small><input value={p.settings.deviceName} onChange={e => p.change("deviceName", e.target.value)} /></label><label className="checkbox"><input type="checkbox" checked={p.settings.allowInsecureHttp} onChange={e => p.change("allowInsecureHttp", e.target.checked)} />암호화되지 않은 HTTP 연결 허용</label><p>같이 사용하는 PC에서도 동일한 서버와 동기화 공간을 지정하세요.</p></fieldset>}
          {section === "password" && <CredentialSettings busy={p.busy || !!activeJob} dirty={p.dirty} playing={!!p.status?.launch} run={p.run} saveSettings={p.save} notify={p.notify} />}
          {section === "emulator" && <fieldset disabled={settingsDisabled}><h2>에뮬레이터 설치본</h2><div className="form-row"><label className="field">설치본<select value={installation.id} onChange={e => setInstallationId(e.target.value)}>{installations.map(i => <option key={i.id} value={i.id}>{i.name} ({i.kind})</option>)}</select></label><button className="secondary" onClick={() => { const id = crypto.randomUUID(); p.change("installations", [...p.settings.installations, { id, name: "Yuzu", kind: "yuzu", build: "", dataRoot: "", nandRoot: "", executable: "", workingDirectory: "", arguments: [] }]); setInstallationId(id); }}>설치본 추가</button></div>
            {installation.id !== "legacy" && <div className="form-row"><label className="field">설치본 이름 <small>필수</small><input value={installation.name} onChange={e => updateInstallation({ name: e.target.value })} /></label><label className="field">종류<select value={installation.kind} onChange={e => updateInstallation({ kind: e.target.value as "yuzu" | "ryujinx" })}><option value="ryujinx">Ryujinx</option><option value="yuzu">Yuzu</option></select></label></div>}
            <label className="field">데이터 폴더 <small>{installation.kind === "yuzu" ? "데이터/NAND 중 하나 필수" : "필수"}</small><input value={installation.dataRoot} placeholder={installation.kind === "ryujinx" ? "/home/deck/.config/Ryujinx" : "/home/deck/.local/share/yuzu"} onChange={e => updateInstallation({ dataRoot: e.target.value })} /></label><p>{installation.kind === "ryujinx" ? "bis/user가 아닌 Ryujinx 최상위 데이터 폴더를 지정하세요." : "기본 NAND는 데이터 폴더/nand입니다. 커스텀 NAND는 고급 옵션에서 지정하세요."}</p>
            <label className="field">실행 파일 <small>Steam 실행 시 필수</small><input value={installation.executable} onChange={e => updateInstallation({ executable: e.target.value })} /></label>
            <button className="text-button" aria-expanded={advanced} onClick={() => setAdvanced(!advanced)}>고급 경로·인수 {advanced ? "접기" : "펼치기"}</button>
            {advanced && <div>{installation.id !== "legacy" && <><label className="field">커스텀 NAND <small>선택</small><input value={installation.nandRoot} onChange={e => updateInstallation({ nandRoot: e.target.value })} /></label><label className="field">빌드 <small>선택</small><input value={installation.build} onChange={e => updateInstallation({ build: e.target.value })} /></label></>}<label className="field">작업 폴더 <small>선택</small><input value={installation.workingDirectory} onChange={e => updateInstallation({ workingDirectory: e.target.value })} /></label><label className="field">실행 인수 <small>한 줄에 하나 · 선택</small><textarea value={installation.arguments.join("\n")} onChange={e => updateInstallation({ arguments: e.target.value.split("\n") })} /></label></div>}
            <button disabled={!installation.name.trim() || (!installation.dataRoot.trim() && !(installation.kind === "yuzu" && installation.nandRoot.trim()))} onClick={() => p.run(() => p.scan(installation.id, p.dirty))}>{p.dirty ? "저장 후 게임 탐색" : "게임 탐색"}</button><p>탐색 결과의 게임 등록은 기존 상세 화면에서 이어집니다.</p>
          </fieldset>}
          {section === "steam" && <><h2>Steam 바로가기 두 가지</h2><p><strong>설정·동기화 화면:</strong> 대상은 ryusync 실행 파일, 실행 옵션은 아래처럼 등록하세요.</p><code className="code-block">deck</code><p>실행할 때 새 인증 링크를 만들고 앱 창을 엽니다. URL이나 토큰을 Steam에 저장하지 않습니다. SteamOS에서는 Chromium/Chrome이 필요하며 Proton 강제 사용은 해제하세요.</p><p><strong>게임 실행 전·후 동기화:</strong></p><code className="code-block">launch --deck</code>{p.profiles.map(profile => <div key={profile.id}><strong>{profile.name}</strong><code className="code-block">launch --deck --launch-profile {profile.id}</code></div>)}<p>충돌·인증 문제로 게임이 보류되면 Deck 창에서 해결한 뒤 Steam 바로가기를 다시 실행하세요. 백엔드를 서비스로 실행하면 창을 닫아도 백엔드를 유지할 수 있습니다.</p><button onClick={() => p.detailed("settings")}>상세 실행 프로필 설정</button></>}
          {settingsDisabled && <p>게임 실행·동기화 작업이 끝나면 설정을 변경할 수 있습니다.</p>}
        </div></section>
      </div>}
    </main>
    <footer className="deck-footer"><span>Tab 이동 · Enter 선택 · Esc 닫기<br /><small>컨트롤러는 Steam Input의 방향키·Enter·Esc 매핑 사용</small></span><div>{page === "sync" && <button className="secondary" onClick={() => p.detailed("history")}>작업 기록</button>}<button className="secondary" onClick={() => p.detailed(page === "sync" ? "overview" : "settings")}>기존 화면</button>{page === "settings" && section !== "password" && <><button disabled={settingsDisabled || !p.dirty} onClick={() => p.run(p.save)}>설정 저장</button>{section === "server" && <button disabled={settingsDisabled || !p.status?.credentialUnlocked} onClick={() => p.run(() => saved(true))}>저장·연결 확인</button>}</>}</div></footer>
  </div>;
}
