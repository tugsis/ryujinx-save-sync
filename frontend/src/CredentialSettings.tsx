import { useEffect, useState } from "react";
import { api } from "./api";

interface CredentialStatus {
  connectionId: string;
  provider: string;
  storeState: string;
  saved: boolean;
  ready: boolean;
  source: string;
  verification: string;
  sessionLocked: boolean;
  environmentConfigured: boolean;
  cleanupPending: boolean;
}
interface Retired { id: string; description: string; cleanupPending: boolean }
const storeLabels: Record<string, string> = {
  available: "사용 가능", missing: "저장 항목을 찾지 못함", locked: "OS 보관함 잠김",
  unavailable: "OS 보관함 이용 불가", denied: "접근 거부", cancelled: "취소 또는 시간 초과",
  error: "보관함 오류", unknown: "확인 전",
};
const sourceLabels: Record<string, string> = {
  none: "입력 필요", session: "이번 실행에서만 사용", environment: "환경변수 사용", "os-store": "OS 보관함의 비밀번호 사용", file: "파일에 저장된 비밀번호 사용",
};
export function CredentialSettings({ busy, dirty, playing, run, saveSettings, notify }: {
  busy: boolean; dirty: boolean; playing: boolean;
  run: (fn: () => Promise<void>) => Promise<void>;
  saveSettings: () => Promise<void>; notify: (value: string) => void;
}) {
  const [status, setStatus] = useState<CredentialStatus | null>(null);
  const [retired, setRetired] = useState<Retired[]>([]);
  const [password, setPassword] = useState("");
  const [persist, setPersist] = useState(false);
  const [storage, setStorage] = useState("os");
  const [loadError, setLoadError] = useState("");
  async function refresh() {
    const [s, rows] = await Promise.all([
      api<CredentialStatus>("/storage/credential"), api<Retired[]>("/storage/credential/retired"),
    ]);
    setStatus(s); setRetired(rows); setLoadError(""); return s;
  }
  useEffect(() => {
    let live = true;
    const update = async () => {
      try {
        const [s, rows] = await Promise.all([api<CredentialStatus>("/storage/credential"), api<Retired[]>("/storage/credential/retired")]);
        if (live) { setStatus(s); setRetired(rows); setLoadError(""); }
      } catch (e) { if (live) setLoadError((e as Error).message); }
    };
    void update(); const timer = setInterval(update, 3000);
    return () => { live = false; clearInterval(timer); };
  }, []);
  const disabled = busy || playing || !status;
  const canSave = storage === "file" || (!!status && ["available", "missing", "locked", "cancelled"].includes(status.storeState));
  async function action(fn: () => Promise<void>) {
    await run(async () => {
      try { await fn(); }
      finally { await refresh().catch(() => {}); }
    });
  }
  async function apply(saveOnDevice: boolean) {
    try {
      if (dirty) await saveSettings();
      const current = await refresh();
      const result = await api<CredentialStatus>("/storage/credential", "PUT", {
        password, persist: saveOnDevice, storage, expectedConnectionId: current.connectionId,
      });
      setStatus(result);
      notify(saveOnDevice
        ? storage === "file" ? "연결을 확인하고 이 기기의 파일에 비밀번호를 저장했습니다." : "연결을 확인하고 이 기기의 OS 보관함에 비밀번호를 저장했습니다."
        : "연결을 확인했습니다. 비밀번호는 이번 실행에서만 사용합니다.");
    } finally { setPassword(""); }
  }
  async function remove(id?: string) {
    if (!status || !window.confirm(id
      ? "선택한 비밀번호 보관 항목을 삭제할까요?"
      : "현재 연결의 저장된 비밀번호를 삭제하고 이 세션의 인증을 잠글까요? 세이브 파일은 삭제되지 않습니다.")) return;
    const path = id ? "/storage/credential/retired/" + id : "/storage/credential";
    await api(path + "?connectionId=" + encodeURIComponent(status.connectionId), "DELETE");
    notify("비밀번호 보관 항목을 삭제했습니다." + (status.environmentConfigured ? " 환경변수는 실행 환경에서 별도로 제거해야 합니다." : ""));
  }
  return <div className="credential-settings">
    <h3>WebDAV 비밀번호</h3>
    <p className="hint">서버 설정 저장과 별도로 연결을 확인한 뒤 비밀번호를 적용합니다.</p>
    {loadError && <p role="alert">{loadError}</p>}
    <label className="field">
      <span>비밀번호 <small>새로 입력·변경할 때 필수</small></span>
      <input type="password" autoComplete="new-password" value={password} disabled={disabled}
        onChange={(e) => setPassword(e.target.value)} placeholder={status?.saved ? "저장값 유지 · 변경할 때만 입력" : "WebDAV 비밀번호 또는 앱 비밀번호"} />
    </label>
    <label className="field"><span>기기에 저장하는 방식</span>
      <select value={storage} disabled={disabled} onChange={e => setStorage(e.target.value)}>
        <option value="os">OS 비밀번호 보관함</option>
        <option value="file">파일에 평문 저장 (개인 기기용)</option>
      </select>
    </label>
    {storage === "file" && <p className="hint">비밀번호를 암호화하지 않은 파일로 저장합니다. 파일을 읽을 수 있는 사람은 비밀번호를 볼 수 있습니다. 별도 보관함 없이 재시작·서비스 실행 시 자동으로 불러옵니다.</p>}
    <label className="checkbox">
      <input type="checkbox" checked={persist} disabled={disabled || (!canSave && !persist)} onChange={(e) => setPersist(e.target.checked)} />
      {storage === "file" ? "이 기기의 파일에 평문 저장" : "이 기기에 안전하게 저장"} <small>선택</small>
    </label>
    <p className="hint">현재 저장소: {status?.provider} · {storeLabels[status?.storeState ?? "unknown"]}.
      {!canSave && " 보관함을 사용할 수 없으면 세션 입력을 사용하세요. 파일 저장 방식을 선택할 수도 있습니다. OS 보관함은 Linux에서 libsecret과 사용자 Secret Service가 필요합니다."}
      {status?.storeState === "locked" && " Desktop Mode에서 OS 보관함 잠금을 해제해야 할 수 있습니다."}
    </p>
    <div className="row-actions credential-actions">
      <button disabled={disabled || !password || (persist && !canSave)} onClick={() => action(() => apply(persist))}>
        {dirty ? "설정 저장·연결 확인 후 적용" : "연결 확인 후 적용"}
      </button>
      {persist && <button className="secondary" disabled={disabled || !password} onClick={() => action(() => apply(false))}>세션에서만 사용</button>}
      <button className="text-button" disabled={disabled} onClick={() => action(async () => {
        await api("/storage/credential/probe", "POST", {}); notify("OS 보관함 상태를 다시 확인했습니다.");
      })}>보관함 다시 확인</button>
    </div>
    {status && <>
      <p className="credential-state" role="status">
        <strong>{status.sessionLocked ? "현재 세션 잠김" : sourceLabels[status.source]}</strong>
        {" · "}{status.saved ? "기기 저장값 있음" : "기기 저장값 없음"}
        {" · "}{{ verified: "연결 확인됨", rejected: "인증 거부 · 재입력 필요", forbidden: "서버 권한 확인 필요", unknown: "연결 미확인" }[status.verification]}
      </p>
      {status.saved && status.source === "session" && <p className="hint">이번 실행에는 입력한 값을 사용하며, 다음 실행에는 기존 기기 저장값을 사용합니다. 영구 변경하려면 저장을 선택하고 다시 적용하세요.</p>}
      {status.environmentConfigured && <p className="hint">시작 환경에 RYUSYNC_WEBDAV_PASSWORD가 있습니다. 다음 실행에는 환경변수가 기기 저장값보다 우선합니다. 기기 저장값만 쓰려면 실행 환경에서 변수를 제거하세요.</p>}
      {status.cleanupPending && <p className="hint">완료되지 않은 저장·삭제 항목이 있습니다. 아래 보관 항목에서 삭제를 재시도할 수 있습니다.</p>}
      <div className="row-actions credential-actions">
        <button className="secondary" disabled={disabled || dirty || !status.saved} onClick={() => action(async () => {
          await api("/storage/credential/unlock", "POST", { expectedConnectionId: status.connectionId });
          notify("저장된 비밀번호를 불러왔습니다. 동기화 또는 연결 테스트로 서버 인증을 확인하세요.");
        })}>저장된 비밀번호 사용</button>
        <button className="secondary" disabled={disabled || !status.ready} onClick={() => action(async () => {
          await api("/storage/credential/lock", "POST", {}); notify("현재 세션의 인증을 잠갔습니다. 다음 실행에서는 저장값을 다시 사용할 수 있습니다.");
        })}>현재 세션 잠금</button>
        <button className="text-button" disabled={disabled || dirty || !status.saved} onClick={() => action(() => remove())}>저장된 비밀번호 삭제</button>
      </div>
      {!!retired.length && <details>
        <summary>이전 연결·정리 대기 보관 항목 ({retired.length})</summary>
        {retired.map((entry) => <div className="credential-retired" key={entry.id}>
          <span>{entry.description}{entry.cleanupPending ? " · 정리 대기" : " · 다른 연결"}</span>
          <button className="text-button" disabled={disabled || dirty} onClick={() => action(() => remove(entry.id))}>삭제</button>
        </div>)}
      </details>}
    </>}
    {dirty && <p className="hint">저장된 비밀번호 사용·삭제 전에 변경한 서버 설정을 저장하세요.</p>}
    {playing && <p className="hint">게임 실행 세션이 끝나면 비밀번호를 변경할 수 있습니다.</p>}
    {busy && <p role="status">처리 중입니다.</p>}
  </div>;
}
