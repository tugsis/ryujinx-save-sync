import React, { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import {
  api,
  type Settings,
  type Save,
  type Binding,
  type Job,
  type Status,
  type Backup,
  type Revision,
  type Outcome,
  type Session,
} from "./api";
import "./style.css";
import { EmulatorSettings } from "./EmulatorSettings";
import type { LaunchProfile, RestoreReservation } from "./api";
const time = (value: string) => new Date(value).toLocaleString("ko-KR");
const labels: Record<string, string> = {
  completed: "완료",
  failed: "실패",
  conflict: "충돌",
  queued: "대기",
  running: "동기화 중",
  waiting: "종료 후 대기",
  blocked: "확인 필요",
  interrupted: "중단됨",
  unchanged: "동일함",
  uploaded: "업로드",
  downloaded: "다운로드",
  empty: "빈 세이브",
  "compatibility-required": "호환성 확인 필요",
  preparing: "실행 준비",
  playing: "플레이 중",
  "post-sync": "종료 후 동기화",
  "needs-attention": "확인 필요",
};
function App() {
  const [ready, setReady] = useState(false),
    [authError, setAuthError] = useState(""),
    [page, setPage] = useState("settings");
  const [settings, setSettings] = useState<Settings | null>(null),
    [status, setStatus] = useState<Status | null>(null),
    [bindings, setBindings] = useState<Binding[]>([]),
    [jobs, setJobs] = useState<Job[]>([]),
    [backups, setBackups] = useState<Backup[]>([]),
    [sessions, setSessions] = useState<Session[]>([]);
  const [profiles, setProfiles] = useState<LaunchProfile[]>([]);
  const [reservations, setReservations] = useState<RestoreReservation[]>([]);
  const [scanId, setScanId] = useState("legacy");
  const [launchId, setLaunchId] = useState("");
  const [gameVersions, setGameVersions] = useState<Record<string, string>>({});
  const [found, setFound] = useState<Save[]>([]),
    [warnings, setWarnings] = useState<string[]>([]),
    [slots, setSlots] = useState<Record<string, string>>({}),
    [candidates, setCandidates] = useState<string[]>([]),
    [catalog, setCatalog] = useState<Revision[] | null>(null);
  const [password, setPassword] = useState(""),
    [argsText, setArgsText] = useState(""),
    [notice, setNotice] = useState(""),
    [busy, setBusy] = useState(false),
    [dirty, setDirty] = useState(false),
    [conflict, setConflict] = useState<Outcome | null>(null);
  async function refresh() {
    const [s, b, j, ba, se, pr, re] = await Promise.all([
      api<Status>("/status"),
      api<Binding[]>("/save-sets"),
      api<Job[]>("/jobs"),
      api<Backup[]>("/backups"),
      api<Session[]>("/launch-sessions"),
      api<LaunchProfile[]>("/launch-profiles"),
      api<RestoreReservation[]>("/restore-reservations"),
    ]);
    setStatus(s);
    setBindings(b);
    setJobs(j);
    setBackups(ba);
    setSessions(se);
    setProfiles(pr);
    setReservations(re);
  }
  useEffect(() => {
    let live = true;
    (async () => {
      try {
        const params = new URLSearchParams(location.hash.slice(1)),
          token = params.get("token");
        if (token) {
          history.replaceState(null, "", location.pathname);
          await api("/session", "POST", { token });
        }
        const s = await api<Settings>("/settings");
        if (!live) return;
        setSettings(s);
        setArgsText(s.arguments.join("\n"));
        setCandidates(await api<string[]>("/ryujinx/candidates"));
        await refresh();
        if (live) setReady(true);
      } catch (e) {
        if (live) setAuthError((e as Error).message);
      }
    })();
    return () => {
      live = false;
    };
  }, []);
  useEffect(() => {
    if (!ready) return;
    let live = true;
    const timer = setInterval(() => {
      if (live) refresh().catch((e) => setNotice((e as Error).message));
    }, 2000);
    return () => {
      live = false;
      clearInterval(timer);
    };
  }, [ready]);
  async function action(fn: () => Promise<void>) {
    setBusy(true);
    setNotice("");
    try {
      await fn();
      await refresh();
    } catch (e) {
      setNotice((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  function change<K extends keyof Settings>(key: K, value: Settings[K]) {
    if (settings) {
      setSettings({ ...settings, [key]: value });
      setDirty(true);
    }
  }
  async function save() {
    if (!settings) return;
    const next = {
      ...settings,
      arguments: argsText.split("\n").filter(Boolean),
    };
    const saved = await api<Settings>("/settings", "PUT", next);
    setSettings(saved);
    setDirty(false);
    if (password) {
      await api("/storage/unlock", "POST", { password });
      setPassword("");
    }
    setNotice("설정을 저장했습니다. 비밀번호는 이 실행 세션에서만 보관합니다.");
  }
  async function sync(id?: string) {
    await api("/sync/jobs", "POST", { bindingId: id ?? null });
    setNotice(
      status?.launch
        ? "게임 종료 후 동기화하도록 요청했습니다."
        : "동기화를 요청했습니다. 작업 기록에서 진행 상황을 확인하세요.",
    );
  }
  async function scan(installationId = scanId) {
    if (dirty) throw new Error("경로 변경 사항을 먼저 저장하세요.");
    const r = await api<{ saves: Save[]; warnings: string[] }>(
      "/installations/" + installationId + "/scan",
      "POST",
      {},
    );
    setScanId(installationId);
    setFound(r.saves);
    setWarnings(r.warnings);
    setPage("games");
    setNotice(`세이브 ${r.saves.length}개를 찾았습니다.`);
  }
  async function resolve(choice: string) {
    if (!conflict?.localHash || !conflict.heads) return;
    await api("/conflicts/" + conflict.bindingId + "/resolve", "POST", {
      choice,
      expectedLocalHash: conflict.localHash,
      expectedHeads: conflict.heads,
    });
    setConflict(null);
    setNotice("선택한 버전으로 충돌 해결을 요청했습니다.");
  }
  if (!ready)
    return (
      <div className="welcome">
        <div className="brand">
          <span>↻</span>RyuSync
        </div>
        <h1>
          {authError
            ? "설정 페이지 연결이 필요해요"
            : "로컬 에이전트에 연결 중…"}
        </h1>
        <p>{authError || "저장된 설정을 불러오고 있습니다."}</p>
        {authError && (
          <>
            <p>
              터미널에 출력된 전체 설정 링크로 열거나 <code>ryusync open</code>
              을 실행하세요.
            </p>
            <button onClick={() => location.reload()}>다시 연결</button>
          </>
        )}
      </div>
    );
  const name = (id: string) => {
    const b = bindings.find((b) => b.id === id);
    return b ? `${b.title} · ${b.emulatorKind} · ${b.slot}` : id;
  };
  const installations = [
    { id: "legacy", name: "기본 Ryujinx", kind: "ryujinx" },
    ...(settings?.installations ?? []),
  ];
  return (
    <div className="app">
      <aside className="sidebar">
        <a
          className="brand"
          href="#"
          onClick={(e) => {
            e.preventDefault();
            setPage("overview");
          }}
        >
          <span>↻</span>RyuSync<small>DEV</small>
        </a>
        <div className="nav-label">내 워크스페이스</div>
        <nav>
          {[
            ["overview", "◫", "대시보드"],
            ["games", "◇", "내 게임"],
            ["history", "◷", "작업 기록"],
            ["backups", "▤", "백업"],
            ["settings", "☷", "설정"],
          ].map(([id, icon, title]) => (
            <button
              key={id}
              className={page === id ? "active" : ""}
              onClick={() => setPage(id)}
            >
              <span>{icon}</span>
              {title}
              {id === "games" && <small>{bindings.length}</small>}
            </button>
          ))}
        </nav>
        <div className="sidebar-status">
          <span className="dot" /> 로컬 백엔드 연결됨
          <small>
            {status?.os} · {status?.version}
          </small>
          <hr />
          {status?.device}
        </div>
      </aside>
      <div className="shell">
        <header>
          <span>
            워크스페이스 <i>/</i>{" "}
            {
              {
                overview: "대시보드",
                games: "내 게임",
                history: "작업 기록",
                backups: "백업",
                settings: "설정",
              }[page]
            }
          </span>
          <span className="badge green">내 기기에서 관리</span>
        </header>
        <main>
          <div className="page-title">
            <div>
              <div className="eyebrow">MAKE YOUR PROGRESS PORTABLE</div>
              <h1>
                {
                  {
                    overview: "대시보드",
                    games: "내 게임",
                    history: "작업 기록",
                    backups: "백업",
                    settings: "설정",
                  }[page]
                }
              </h1>
              <p>어느 기기에서든, 마지막으로 플레이한 곳부터.</p>
            </div>
            <button
              disabled={busy || !bindings.length}
              onClick={() => action(() => sync())}
            >
              ↻ 전체 동기화
            </button>
          </div>
          <div className="dev-note">
            개발 초기 버전 · 실제 파일을 읽고 동기화합니다. 먼저 복제한 테스트
            세이브로 확인하세요.
          </div>
          {notice && (
            <div className="notice" role="status">
              {notice}
              <button aria-label="알림 닫기" onClick={() => setNotice("")}>
                ×
              </button>
            </div>
          )}
          {status?.recovery !== 0 && (
            <div className="warning">
              미완료 적용 {status?.recovery}건이 있어 동기화가 차단되었습니다.
              복구 문서와 백업을 확인하세요.
            </div>
          )}
          {page === "settings" && settings && (
            <div className="settings-layout">
              <div>
                <section className="card">
                  <div className="card-head">
                    <span className="section-symbol">▣</span>
                    <div>
                      <h2>내 기기</h2>
                      <p>두 기기의 설정을 각각 등록하세요.</p>
                    </div>
                  </div>
                  <div className="card-body">
                    <div className="form-row">
                      <Field label="기기 이름">
                        <input
                          value={settings.deviceName}
                          onChange={(e) => change("deviceName", e.target.value)}
                        />
                      </Field>
                      <Field label="운영체제">
                        <div className="readonly">{status?.os}</div>
                      </Field>
                    </div>
                  </div>
                </section>
                <section className="card">
                  <div className="card-head">
                    <span className="section-symbol">☁</span>
                    <div>
                      <h2>클라우드 저장소</h2>
                      <p>두 기기에서 같은 서버와 동기화 공간을 설정하세요.</p>
                    </div>
                    <span className="badge">WebDAV</span>
                  </div>
                  <div className="card-body">
                    <div className="providers">
                      <div className="provider selected">
                        <strong>☁ WebDAV</strong>
                        <small>개인 서버 또는 NAS</small>
                      </div>
                      <div className="provider disabled">
                        <strong>△ Google Drive</strong>
                        <small>다음 단계에 제공 예정</small>
                      </div>
                    </div>
                    <Field label="WebDAV 서버 주소">
                      <input
                        type="url"
                        placeholder="https://nas.example.com/webdav/"
                        value={settings.webDavUrl}
                        onChange={(e) => change("webDavUrl", e.target.value)}
                      />
                    </Field>
                    <div className="form-row">
                      <Field label="사용자 이름">
                        <input
                          value={settings.webDavUser}
                          onChange={(e) => change("webDavUser", e.target.value)}
                        />
                      </Field>
                      <Field label="비밀번호 · 실행 종료 시 삭제">
                        <input
                          type="password"
                          autoComplete="off"
                          value={password}
                          placeholder={
                            status?.credentialUnlocked
                              ? "연결 잠금 해제됨"
                              : "앱 비밀번호 입력"
                          }
                          onChange={(e) => setPassword(e.target.value)}
                        />
                      </Field>
                    </div>
                    <Field label="동기화 공간 이름">
                      <input
                        value={settings.workspace}
                        onChange={(e) => change("workspace", e.target.value)}
                      />
                      <small>
                        두 기기에 같은 이름을 입력하세요. 서버의{" "}
                        {settings.protocolVersion === 2
                          ? "ryusync-v2"
                          : "ryusync"}
                        /이름 아래에 보관합니다.
                      </small>
                    </Field>
                    <label className="checkbox">
                      <input
                        type="checkbox"
                        checked={settings.allowInsecureHttp}
                        onChange={(e) =>
                          change("allowInsecureHttp", e.target.checked)
                        }
                      />
                      암호화되지 않은 HTTP 연결 허용
                    </label>
                    <div className="section-actions">
                      <span
                        className={
                          "badge " + (status?.credentialUnlocked ? "green" : "")
                        }
                      >
                        {status?.credentialUnlocked
                          ? "비밀번호 잠금 해제됨"
                          : "비밀번호 입력 필요"}
                      </span>
                      <button
                        className="secondary"
                        disabled={busy}
                        onClick={() =>
                          action(async () => {
                            await save();
                            await api("/storage/test", "POST", {});
                            setNotice(
                              "실제 WebDAV 연결 테스트를 완료했습니다.",
                            );
                          })
                        }
                      >
                        저장 후 연결 테스트 →
                      </button>
                    </div>
                    <p className="hint">
                      비밀번호는 디스크에 저장하지 않습니다. OS 자격 증명 저장
                      기능은 후속 단계입니다.
                    </p>
                  </div>
                </section>
                <section className="card">
                  <div className="card-head">
                    <span className="section-symbol">◇</span>
                    <div>
                      <h2>게임 자동 탐색</h2>
                      <p>세이브 인덱스에서 게임·사용자·저장 위치를 읽습니다.</p>
                    </div>
                  </div>
                  <div className="card-body">
                    <Field label="Ryujinx 데이터 폴더">
                      <input
                        placeholder="Ryujinx 데이터 루트"
                        list="roots"
                        value={settings.dataRoot}
                        onChange={(e) => change("dataRoot", e.target.value)}
                      />
                      <datalist id="roots">
                        {candidates.map((c) => (
                          <option key={c} value={c} />
                        ))}
                      </datalist>
                    </Field>
                    <div className="section-actions">
                      <span className="hint">
                        게임마다 세이브 경로를 입력하지 않아도 됩니다.
                      </span>
                      <button
                        className="secondary"
                        disabled={busy || dirty || !settings.dataRoot}
                        onClick={() => action(() => scan("legacy"))}
                      >
                        ↻ 게임 탐색
                      </button>
                    </div>
                  </div>
                </section>
                <section className="card">
                  <div className="card-head">
                    <span className="section-symbol">▷</span>
                    <div>
                      <h2>Steam 실행 전·후 동기화</h2>
                      <p>
                        Steam의 비 Steam 게임 대상으로 RyuSync를 등록하세요.
                      </p>
                    </div>
                  </div>
                  <div className="card-body">
                    <Field label="Ryujinx 실행 파일 · 실제 네이티브 실행 파일">
                      <input
                        value={settings.executable}
                        onChange={(e) => change("executable", e.target.value)}
                        placeholder="Ryujinx.exe 또는 Linux 실행 파일"
                      />
                    </Field>
                    <Field label="작업 디렉터리 · 비우면 실행 파일 폴더">
                      <input
                        value={settings.workingDirectory}
                        onChange={(e) =>
                          change("workingDirectory", e.target.value)
                        }
                      />
                    </Field>
                    <Field label="실행 인수 · 한 줄에 인수 하나씩">
                      <textarea
                        rows={3}
                        value={argsText}
                        onChange={(e) => {
                          setArgsText(e.target.value);
                          setDirty(true);
                        }}
                        placeholder={"-r\n데이터 폴더 경로"}
                      />
                    </Field>
                    <div className="flow">
                      서버와 동기화 <b>→</b> Ryujinx 실행 <b>→</b> 종료 후
                      동기화
                    </div>
                    <Field label="Steam 실행 옵션">
                      <code className="code-block">launch</code>
                      <small>
                        Steam 대상은 배포 폴더의 ryusync 실행 파일로 지정합니다.
                        동기화 실패 시 게임 실행을 보류합니다.
                      </small>
                    </Field>
                    <p className="hint">
                      현재 개발판은 실패 시 브라우저에서 해결합니다. Gaming Mode
                      전용 컨트롤러 창과 오프라인 실행 선택은 아직 구현
                      전입니다.
                    </p>
                  </div>
                </section>
                <EmulatorSettings
                  settings={settings}
                  bindings={bindings}
                  profiles={profiles}
                  busy={busy}
                  dirty={dirty}
                  change={(value) => change("installations", value)}
                  action={action}
                  scan={scan}
                  imported={async () => {
                    const value = await api<Settings>("/settings");
                    setSettings(value);
                    setArgsText(value.arguments.join("\n"));
                    setDirty(false);
                    setFound([]);
                    setNotice(
                      "설정과 연결을 가져왔습니다. 비밀번호를 다시 입력한 뒤 v1 서버 버전을 가져오거나 로컬 세이브를 동기화하세요.",
                    );
                  }}
                />
                <div className="save-bar">
                  <span>
                    {dirty
                      ? "저장하지 않은 변경 사항이 있습니다."
                      : "설정은 이 기기에 저장됩니다."}
                  </span>
                  <button disabled={busy} onClick={() => action(save)}>
                    ✓ 설정 저장
                  </button>
                </div>
              </div>
              <aside className="context">
                <div className="flow-card">
                  <span className="eyebrow">YOUR SAVE, EVERYWHERE</span>
                  <h2>
                    기기는 바뀌어도
                    <br />
                    모험은 계속.
                  </h2>
                  <div className="device-tile">
                    ▣<strong>Windows PC</strong>
                  </div>
                  <div className="dashed" />
                  <span className="cloud">☁</span>
                  <div className="dashed" />
                  <div className="device-tile">
                    ◇<strong>Steam Deck</strong>
                  </div>
                  <p>
                    서버와 로컬이 각각 바뀌면
                    <br />두 버전을 모두 보존합니다.
                  </p>
                </div>
                <div className="hint-box">
                  <h3>플레이 중에는 안전하게</h3>
                  <p>
                    실행 중 동기화 요청은 종료 후 대기합니다. 세이브 적용 전에
                    기존 폴더를 백업합니다.
                  </p>
                </div>
              </aside>
            </div>
          )}
          {page === "overview" && (
            <>
              <div className="stats">
                <div>
                  <small>동기화 대상</small>
                  <strong>
                    {bindings.length}
                    <span>게임·프로필</span>
                  </strong>
                </div>
                <div>
                  <small>미전송 스냅샷</small>
                  <strong>
                    {status?.pendingUploads ?? 0}
                    <span>다음 동기화에서 재시도</span>
                  </strong>
                </div>
                <div>
                  <small>현재 실행 상태</small>
                  <strong className="stat-text">
                    {status?.launch ? labels[status.launch.status] : "대기 중"}
                  </strong>
                </div>
              </div>
              <section className="card">
                <div className="card-head">
                  <div>
                    <h2>플레이 준비</h2>
                    <p>
                      실행 프로필에 연결된 게임 그룹을 비교한 뒤 선택한
                      에뮬레이터를 실행합니다.
                    </p>
                  </div>
                </div>
                <div className="card-body">
                  <div className="flow">
                    동기화 확인 → 플레이 → 종료 후 업로드
                  </div>
                  <Field label="실행 프로필">
                    <select
                      value={launchId}
                      onChange={(e) => setLaunchId(e.target.value)}
                    >
                      <option value="">기본 Ryujinx</option>
                      {profiles.map((p) => (
                        <option key={p.id} value={p.id}>
                          {p.name}
                        </option>
                      ))}
                    </select>
                  </Field>
                  <button
                    disabled={busy || !!status?.launch}
                    onClick={() =>
                      action(async () => {
                        await api("/launch-sessions", "POST", {
                          launchProfileId: launchId || null,
                        });
                        setNotice("실행 전 동기화를 시작했습니다.");
                      })
                    }
                  >
                    ▷ 동기화 후 에뮬레이터 실행
                  </button>
                  {sessions[0] && (
                    <div className="session-state">
                      <span className="badge">
                        {labels[sessions[0].status] ?? sessions[0].status}
                      </span>
                      <p>{sessions[0].message}</p>
                    </div>
                  )}
                </div>
              </section>
              <section className="card">
                <div className="card-head">
                  <div>
                    <h2>최근 작업</h2>
                    <p>실제 백엔드 작업 상태입니다.</p>
                  </div>
                  <button
                    className="secondary"
                    onClick={() => setPage("history")}
                  >
                    전체 보기
                  </button>
                </div>
                <div className="card-body">
                  {jobs.slice(0, 3).map((j) => (
                    <JobView
                      key={j.id}
                      job={j}
                      name={name}
                      resolve={setConflict}
                    />
                  ))}
                  {!jobs.length && (
                    <p className="empty">
                      게임을 탐색하고 동기화 대상을 등록해 주세요.
                    </p>
                  )}
                </div>
              </section>
            </>
          )}
          {page === "games" && (
            <>
              <section className="card">
                <div className="card-head">
                  <div>
                    <h2>로컬 세이브 탐색</h2>
                    <p>
                      프로필 연결 이름은 두 기기에서 동일하게 지정하세요.
                      계정마다 다른 이름을 사용합니다.
                    </p>
                  </div>
                  <button
                    className="secondary"
                    disabled={busy}
                    onClick={() => action(scan)}
                  >
                    ↻ 다시 탐색
                  </button>
                </div>
                <div className="card-body">
                  <Field label="탐색·서버 복원 대상 설치본">
                    <select
                      value={scanId}
                      onChange={(e) => {
                        setScanId(e.target.value);
                        setFound([]);
                        setWarnings([]);
                      }}
                    >
                      {installations.map((i) => (
                        <option key={i.id} value={i.id}>
                          {i.name} ({i.kind})
                        </option>
                      ))}
                    </select>
                  </Field>
                  {reservations
                    .filter((r) => r.installationId === scanId)
                    .map((r) => (
                      <div className="warning" key={r.id}>
                        {r.titleId} · {r.slot}: 서버 복원 예약됨. 해당 프로필로
                        게임을 수동 실행·저장·종료하고 다시 탐색한 뒤 ‘예약 복원
                        연결’을 누르세요.
                        <button
                          className="text-button"
                          disabled={busy}
                          onClick={() =>
                            action(async () => {
                              await api(
                                "/restore-reservations/" + r.id,
                                "DELETE",
                              );
                            })
                          }
                        >
                          예약 해제
                        </button>
                      </div>
                    ))}
                  {warnings.map((w) => (
                    <div className="warning" key={w}>
                      {w}
                    </div>
                  ))}
                  {!found.length && (
                    <p className="empty">
                      설정에서 설치본의 데이터/NAND 폴더를 저장하고 선택한
                      설치본을 탐색하세요.
                    </p>
                  )}
                  {found.map((s) => (
                    <div className="game-row" key={s.id}>
                      <span className="game-icon">◇</span>
                      <div className="game-info">
                        <h3>{s.title}</h3>
                        <code>{s.titleId}</code>
                        <small>
                          {s.emulatorKind} · 프로필: {s.profileName}
                        </small>
                        <small>
                          {s.status === "ready"
                            ? "탐색 완료"
                            : "게임 초기화 필요"}
                        </small>
                        <small className="file-path">{s.contentPath}</small>
                      </div>
                      <div className="game-controls">
                        <input
                          aria-label={s.title + " 프로필 연결 이름"}
                          placeholder="프로필 연결 이름"
                          value={
                            slots[s.id] ??
                            reservations.find(
                              (r) =>
                                r.installationId === s.installationId &&
                                r.titleId === s.titleId,
                            )?.slot ??
                            "main"
                          }
                          onChange={(e) =>
                            setSlots({ ...slots, [s.id]: e.target.value })
                          }
                        />
                        <input
                          aria-label={s.title + " 게임 버전"}
                          placeholder="게임 버전 (선택)"
                          value={gameVersions[s.id] ?? ""}
                          onChange={(e) =>
                            setGameVersions({
                              ...gameVersions,
                              [s.id]: e.target.value,
                            })
                          }
                        />
                        {bindings.some((b) => b.id === s.id) ? (
                          <span className="badge green">등록됨</span>
                        ) : (
                          <button
                            disabled={busy || s.status !== "ready"}
                            onClick={() =>
                              action(async () => {
                                await api("/save-sets", "POST", {
                                  discoveryId: s.id,
                                  installationId: s.installationId,
                                  gameVersion: gameVersions[s.id] ?? "",
                                  restoreReservationId:
                                    reservations.find(
                                      (r) =>
                                        r.installationId === s.installationId &&
                                        r.titleId === s.titleId,
                                    )?.id ?? null,
                                  slot:
                                    slots[s.id] ??
                                    reservations.find(
                                      (r) =>
                                        r.installationId === s.installationId &&
                                        r.titleId === s.titleId,
                                    )?.slot ??
                                    "main",
                                });
                                setNotice("동기화 대상으로 등록했습니다.");
                              })
                            }
                          >
                            {reservations.some(
                              (r) =>
                                r.installationId === s.installationId &&
                                r.titleId === s.titleId,
                            )
                              ? "예약 복원 연결"
                              : "등록"}
                          </button>
                        )}
                      </div>
                    </div>
                  ))}
                </div>
              </section>
              <section className="card">
                <div className="card-head">
                  <div>
                    <h2>동기화 대상</h2>
                    <p>
                      처음 연결한 양쪽 세이브가 다르면 사용할 버전을 선택합니다.
                    </p>
                  </div>
                </div>
                <div className="card-body">
                  {bindings.map((b) => (
                    <div className="binding-row" key={b.id}>
                      <div>
                        <h3>{b.title}</h3>
                        <small>
                          {b.titleId} · {b.slot} · {b.emulatorKind}{" "}
                          {b.emulatorBuild} ·{" "}
                          {b.gameVersion || "게임 버전 미확인"}
                        </small>
                      </div>
                      <div className="row-actions">
                        <button
                          className="secondary"
                          disabled={busy}
                          onClick={() => action(() => sync(b.id))}
                        >
                          지금 동기화
                        </button>
                        <button
                          className="secondary"
                          disabled={busy}
                          onClick={() =>
                            action(async () => {
                              const outcome = await api<Outcome>(
                                "/save-sets/" + b.id + "/compare",
                                "POST",
                                {},
                              );
                              if (outcome.status === "compatible")
                                setNotice(
                                  "다른 에뮬레이터의 이력이 없어 별도 교차 승인이 필요하지 않습니다.",
                                );
                              else setConflict(outcome);
                            })
                          }
                        >
                          교차 호환성 확인
                        </button>
                        <button
                          className="text-button"
                          disabled={busy}
                          onClick={() =>
                            action(async () => {
                              await api("/save-sets/" + b.id, "DELETE");
                              setNotice(
                                "동기화 연결을 해제했습니다. 세이브 파일은 보존됩니다.",
                              );
                            })
                          }
                        >
                          연결 해제
                        </button>
                      </div>
                    </div>
                  ))}
                  {!bindings.length && (
                    <p className="empty">등록된 게임이 없습니다.</p>
                  )}
                </div>
              </section>
              <section className="card">
                <div className="card-head">
                  <div>
                    <h2>서버의 게임</h2>
                    <p>
                      로컬 세이브가 없으면 해당 게임을 한 번 실행해 저장 위치를
                      생성하세요.
                    </p>
                  </div>
                  <button
                    className="secondary"
                    disabled={busy}
                    onClick={() =>
                      action(async () =>
                        setCatalog(await api<Revision[]>("/catalog")),
                      )
                    }
                  >
                    서버 목록 조회
                  </button>
                </div>
                <div className="card-body">
                  {catalog === null ? (
                    <p className="empty">
                      서버의 게임 목록을 조회할 수 있습니다.
                    </p>
                  ) : catalog.length === 0 ? (
                    <p className="empty">서버에 저장된 세이브가 없습니다.</p>
                  ) : (
                    catalog.map((r) => (
                      <div className="binding-row" key={r.id}>
                        <div>
                          <h3>{r.title || r.titleId}</h3>
                          <small>
                            {r.titleId} · {r.slot} · {r.deviceName} ·{" "}
                            {r.sourceEmulator} {r.sourceBuild}
                          </small>
                        </div>
                        <button
                          className="secondary"
                          disabled={
                            busy ||
                            dirty ||
                            bindings.some(
                              (b) =>
                                b.installationId === scanId &&
                                b.titleId === r.titleId,
                            ) ||
                            reservations.some(
                              (re) =>
                                re.installationId === scanId &&
                                re.titleId === r.titleId,
                            )
                          }
                          onClick={() =>
                            action(async () => {
                              await api("/restore-reservations", "POST", {
                                installationId: scanId,
                                revisionId: r.id,
                              });
                              setNotice(
                                "서버 복원을 예약했습니다. 해당 게임을 수동 실행·저장·종료하고 탐색한 뒤 예약 복원 연결을 등록하세요.",
                              );
                            })
                          }
                        >
                          선택한 설치본에 복원 예약
                        </button>
                        <span className="badge">
                          {bindings.some((b) => b.remoteKey === r.saveKey)
                            ? "연결됨"
                            : "로컬 탐색 후 연결"}
                        </span>
                      </div>
                    ))
                  )}
                </div>
              </section>
            </>
          )}
          {page === "history" && (
            <section className="card">
              <div className="card-head">
                <div>
                  <h2>동기화 작업</h2>
                  <p>Steam과 웹페이지의 요청을 한곳에서 확인하세요.</p>
                </div>
              </div>
              <div className="card-body">
                {jobs.map((j) => (
                  <JobView
                    key={j.id}
                    job={j}
                    name={name}
                    resolve={setConflict}
                  />
                ))}
                {!jobs.length && (
                  <p className="empty">아직 작업 기록이 없습니다.</p>
                )}
              </div>
            </section>
          )}
          {page === "backups" && (
            <section className="card">
              <div className="card-head">
                <div>
                  <h2>적용 전 백업</h2>
                  <p>
                    세이브 루트 전체를 별도 폴더에 보존합니다. 현재 개발판은
                    자동 삭제하지 않습니다.
                  </p>
                </div>
              </div>
              <div className="card-body">
                {backups.map((b) => (
                  <div className="backup-row" key={b.id}>
                    <h3>{b.title}</h3>
                    <small>{time(b.createdAt)}</small>
                    <code className="file-path">{b.path}</code>
                    <p className="hint">복구 방법: docs/recovery.md</p>
                  </div>
                ))}
                {!backups.length && (
                  <p className="empty">
                    서버 세이브를 적용하면 현재 로컬 세이브의 백업이 여기에
                    표시됩니다.
                  </p>
                )}
              </div>
            </section>
          )}
          <footer>
            RyuSync <span>당신의 다음 플레이를 연결합니다.</span>
            <small>Windows 11 & SteamOS</small>
          </footer>
        </main>
      </div>
      {conflict && (
        <div className="modal-backdrop">
          <section
            role="dialog"
            aria-modal="true"
            aria-labelledby="conflict-title"
            className="modal"
          >
            <h2 id="conflict-title">
              {conflict.status === "compatibility-required"
                ? "교차 에뮬레이터 세이브 확인"
                : "진행 상황이 두 갈래로 나뉘었어요"}
            </h2>
            <p>{name(conflict.bindingId)}</p>
            {conflict.status === "compatibility-required" && (
              <>
                <p>{conflict.message}</p>
                <div className="warning">
                  같은 Title ID여도 게임 버전·모드·계정에 따라 호환되지 않을 수
                  있습니다. 시험 적용 승인 후 다시 동기화하고, 에뮬레이터를 수동
                  실행해 실제 로드·저장·종료를 확인하세요. 확인 전 Steam 자동
                  실행은 차단됩니다.
                </div>
                {conflict.versions?.map((v) => (
                  <p key={v.id}>
                    {v.deviceName} · {v.sourceEmulator} {v.sourceBuild} · 게임{" "}
                    {v.gameVersion || "버전 미확인"}
                  </p>
                ))}
                {(
                  [
                    ["trial", "백업 후 시험 적용 승인"],
                    ["confirmed", "게임에서 로드·저장·종료 확인 완료"],
                  ] as const
                ).map(([state, label]) => (
                  <button
                    className="version-button"
                    key={state}
                    disabled={busy}
                    onClick={() =>
                      action(async () => {
                        await api(
                          "/save-sets/" + conflict.bindingId + "/compatibility",
                          "POST",
                          {
                            state,
                            expectedLocalHash: conflict.localHash,
                            expectedHeads: conflict.heads,
                          },
                        );
                        setConflict(null);
                        setNotice(
                          state === "trial"
                            ? "시험 적용을 승인했습니다. 다시 동기화하여 사용할 원본을 선택하세요."
                            : "교차 호환성 확인을 기록했습니다.",
                        );
                      })
                    }
                  >
                    {label}
                  </button>
                ))}
              </>
            )}
            {conflict.status !== "compatibility-required" && (
              <>
                <div className="warning">
                  선택하지 않은 로컬 내용도 서버 버전과 백업으로 보존합니다.
                  상태가 바뀌면 다시 비교합니다.
                </div>
                <button
                  disabled={busy}
                  className="version-button"
                  onClick={() => action(() => resolve("local"))}
                >
                  <strong>이 기기의 세이브 사용</strong>
                  <small>현재 로컬 진행 상황으로 통일</small>
                </button>
                {conflict.versions?.map((v) => (
                  <button
                    key={v.id}
                    disabled={busy}
                    className="version-button"
                    onClick={() => action(() => resolve(v.id))}
                  >
                    <strong>{v.deviceName}의 세이브 사용</strong>
                    <small>
                      {time(v.createdAt)} · {v.sourceEmulator} {v.sourceBuild} ·{" "}
                      {v.id.slice(0, 8)}
                    </small>
                  </button>
                ))}
              </>
            )}
            <button
              className="secondary"
              disabled={busy}
              onClick={() => setConflict(null)}
            >
              나중에 선택
            </button>
          </section>
        </div>
      )}
    </div>
  );
}
function Field({
  label,
  children,
}: {
  label: string;
  children: React.ReactNode;
}) {
  return (
    <label className="field">
      <span>{label}</span>
      {children}
    </label>
  );
}
function JobView({
  job,
  name,
  resolve,
}: {
  job: Job;
  name: (id: string) => string;
  resolve: (r: Outcome) => void;
}) {
  return (
    <article className="job">
      <div className="job-heading">
        <span
          className={
            "badge " +
            (job.status === "completed"
              ? "green"
              : job.status === "conflict"
                ? "amber"
                : "")
          }
        >
          {labels[job.status] ?? job.status}
        </span>
        <strong>{job.message}</strong>
        <small>
          {time(job.createdAt)} · {job.source}
        </small>
      </div>
      {job.results.map((r) => (
        <div key={r.bindingId} className="result">
          <div>
            <strong>{name(r.bindingId)}</strong>
            <p>{r.message}</p>
          </div>
          {(r.status === "conflict" ||
            r.status === "compatibility-required") && (
            <button className="secondary" onClick={() => resolve(r)}>
              {r.status === "compatibility-required"
                ? "호환성 확인"
                : "버전 선택"}
            </button>
          )}
        </div>
      ))}
    </article>
  );
}
createRoot(document.getElementById("root")!).render(<App />);
