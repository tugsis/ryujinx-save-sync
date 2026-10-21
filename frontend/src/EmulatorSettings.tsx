import { useState } from "react";
import {
  api,
  type Settings,
  type Installation,
  type Binding,
  type LaunchProfile,
  type Revision,
} from "./api";

type Props = {
  settings: Settings;
  bindings: Binding[];
  profiles: LaunchProfile[];
  busy: boolean;
  dirty: boolean;
  change: (installations: Installation[]) => void;
  action: (fn: () => Promise<void>) => Promise<void>;
  scan: (installationId: string) => Promise<void>;
  imported: () => Promise<void>;
};
export function EmulatorSettings({
  settings,
  bindings,
  profiles,
  busy,
  dirty,
  change,
  action,
  scan,
  imported,
}: Props) {
  const [profile, setProfile] = useState<LaunchProfile>({
    id: "",
    name: "",
    installationId: "legacy",
    groupKeys: [],
    arguments: [],
  });
  const [sourceRoot, setSourceRoot] = useState("");
  const [legacy, setLegacy] = useState<Revision[] | null>(null);
  const [candidateRoots, setCandidateRoots] = useState<
    Record<string, string[]>
  >({});
  const [profileArgs, setProfileArgs] = useState("");
  const installations: Installation[] = [
    {
      id: "legacy",
      name: "기본 Ryujinx",
      kind: "ryujinx",
      build: "",
      dataRoot: settings.dataRoot,
      nandRoot: "",
      executable: settings.executable,
      workingDirectory: settings.workingDirectory,
      arguments: settings.arguments,
    },
    ...settings.installations,
  ];
  function update(id: string, patch: Partial<Installation>) {
    change(
      settings.installations.map((i) => (i.id === id ? { ...i, ...patch } : i)),
    );
  }
  return (
    <>
      <section className="card">
        <div className="card-head">
          <div>
            <h2>추가 에뮬레이터 설치본</h2>
            <p>
              Yuzu·추가 Ryujinx를 등록하고 데이터/NAND 루트를 한 번 지정하세요.
            </p>
          </div>
        </div>
        <div className="card-body">
          {settings.protocolVersion !== 2 && (
            <div className="warning">
              이 상태 폴더는 v1입니다. Yuzu는 새 v2 상태 폴더에서 설정하거나
              이전 설정을 가져온 뒤 사용하세요.
            </div>
          )}
          {settings.installations.map((i) => (
            <div className="installation-editor" key={i.id}>
              <div className="form-row">
                <label className="field">
                  <span>설치본 이름</span>
                  <input
                    value={i.name}
                    onChange={(e) => update(i.id, { name: e.target.value })}
                  />
                </label>
                <label className="field">
                  <span>에뮬레이터</span>
                  <select
                    value={i.kind}
                    onChange={(e) =>
                      update(i.id, {
                        kind: e.target.value as Installation["kind"],
                      })
                    }
                  >
                    <option value="ryujinx">Ryujinx</option>
                    <option value="yuzu">Yuzu</option>
                  </select>
                </label>
              </div>
              <label className="field">
                <span>에뮬레이터 빌드 · 직접 확인한 버전</span>
                <input
                  value={i.build}
                  onChange={(e) => update(i.id, { build: e.target.value })}
                  placeholder="예: 사용 중인 빌드 번호"
                />
              </label>
              <label className="field">
                <span>데이터 루트</span>
                <input
                  list={"roots-" + i.id}
                  value={i.dataRoot}
                  onChange={(e) => update(i.id, { dataRoot: e.target.value })}
                />
                <datalist id={"roots-" + i.id}>
                  {candidateRoots[i.id]?.map((root) => (
                    <option key={root} value={root} />
                  ))}
                </datalist>
              </label>
              {i.kind === "yuzu" && (
                <label className="field">
                  <span>커스텀 NAND 루트 · 비우면 데이터 루트/nand</span>
                  <input
                    value={i.nandRoot}
                    onChange={(e) => update(i.id, { nandRoot: e.target.value })}
                  />
                </label>
              )}
              <label className="field">
                <span>
                  실제 실행 파일 · EXE 또는 직접 실행 가능한 Linux 바이너리
                </span>
                <input
                  value={i.executable}
                  onChange={(e) => update(i.id, { executable: e.target.value })}
                />
              </label>
              <label className="field">
                <span>작업 디렉터리 · 선택</span>
                <input
                  value={i.workingDirectory}
                  onChange={(e) =>
                    update(i.id, { workingDirectory: e.target.value })
                  }
                />
              </label>
              <label className="field">
                <span>기본 실행 인수 · 한 줄에 하나</span>
                <textarea
                  rows={3}
                  value={i.arguments.join("\n")}
                  onChange={(e) =>
                    update(i.id, { arguments: e.target.value.split("\n") })
                  }
                />
              </label>
              <div className="row-actions">
                <button
                  className="secondary"
                  disabled={busy}
                  onClick={() =>
                    action(async () =>
                      setCandidateRoots({
                        ...candidateRoots,
                        [i.id]: await api<string[]>(
                          "/emulators/" +
                            i.kind +
                            "/candidates?executable=" +
                            encodeURIComponent(i.executable),
                        ),
                      }),
                    )
                  }
                >
                  경로 후보 찾기
                </button>
                <button
                  disabled={busy || dirty}
                  onClick={() => action(() => scan(i.id))}
                >
                  저장된 설치본 탐색
                </button>
                <button
                  className="text-button"
                  disabled={
                    busy ||
                    bindings.some((b) => b.installationId === i.id) ||
                    profiles.some((p) => p.installationId === i.id)
                  }
                  onClick={() =>
                    change(
                      settings.installations.filter(
                        (other) => other.id !== i.id,
                      ),
                    )
                  }
                >
                  설치본 삭제
                </button>
              </div>
              <small className="hint">
                연결된 게임이 있으면 종류·경로·빌드는 변경할 수 없습니다.
                AppImage·Flatpak 자동 실행은 아직 지원하지 않으며 종료 후 웹
                동기화는 가능합니다.
              </small>
            </div>
          ))}
          <button
            className="secondary"
            disabled={busy || settings.protocolVersion !== 2}
            onClick={() =>
              change([
                ...settings.installations,
                {
                  id: crypto.randomUUID(),
                  name: "Yuzu",
                  kind: "yuzu",
                  build: "",
                  dataRoot: "",
                  nandRoot: "",
                  executable: "",
                  workingDirectory: "",
                  arguments: [],
                },
              ])
            }
          >
            ＋ 설치본 추가
          </button>
          <p className="hint">
            추가·변경한 뒤 아래 ‘설정 저장’을 눌러주세요. 세이브 내부의 프로필
            ID를 바꾸거나 프로필 DB를 복사하지 않습니다.
          </p>
        </div>
      </section>
      <section className="card">
        <div className="card-head">
          <div>
            <h2>Steam 실행 프로필</h2>
            <p>설치본과 동기화할 게임 그룹을 선택하세요.</p>
          </div>
        </div>
        <div className="card-body">
          <label className="field">
            <span>편집할 프로필</span>
            <select
              value={profile.id}
              onChange={(e) => {
                const selected = profiles.find(
                  (p) => p.id === e.target.value,
                ) ?? {
                  id: "",
                  name: "",
                  installationId: "legacy",
                  groupKeys: [],
                  arguments: [],
                };
                setProfile(selected);
                setProfileArgs(selected.arguments.join("\n"));
              }}
            >
              <option value="">새 프로필</option>
              {profiles.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          </label>
          <label className="field">
            <span>프로필 이름</span>
            <input
              value={profile.name}
              onChange={(e) => setProfile({ ...profile, name: e.target.value })}
            />
          </label>
          <label className="field">
            <span>실행할 설치본</span>
            <select
              value={profile.installationId}
              onChange={(e) =>
                setProfile({
                  ...profile,
                  installationId: e.target.value,
                  groupKeys: [],
                })
              }
            >
              {installations.map((i) => (
                <option key={i.id} value={i.id}>
                  {i.name} ({i.kind})
                </option>
              ))}
            </select>
          </label>
          <p className="hint">
            선택하지 않으면 해당 설치본의 등록 게임 전체를 동기화합니다. 같은
            슬롯의 다른 로컬 설치본도 함께 비교합니다.
          </p>
          {bindings
            .filter((b) => b.installationId === profile.installationId)
            .map((b) => (
              <label className="checkbox" key={b.id}>
                <input
                  type="checkbox"
                  checked={profile.groupKeys.includes(b.remoteKey)}
                  onChange={(e) =>
                    setProfile({
                      ...profile,
                      groupKeys: e.target.checked
                        ? [...profile.groupKeys, b.remoteKey]
                        : profile.groupKeys.filter((k) => k !== b.remoteKey),
                    })
                  }
                />
                {b.title} · {b.slot}
              </label>
            ))}
          <label className="field">
            <span>프로필 추가 인수 · 한 줄에 하나</span>
            <textarea
              rows={3}
              value={profileArgs}
              onChange={(e) => setProfileArgs(e.target.value)}
            />
          </label>
          <button
            disabled={busy || dirty || !profile.name}
            onClick={() =>
              action(async () => {
                const id = profile.id || crypto.randomUUID();
                const saved = await api<LaunchProfile>(
                  "/launch-profiles/" + id,
                  "PUT",
                  {
                    ...profile,
                    id,
                    arguments: profileArgs.split("\n").filter(Boolean),
                  },
                );
                setProfile(saved);
              })
            }
          >
            실행 프로필 저장
          </button>
          {profile.id && (
            <code className="code-block">
              launch --launch-profile {profile.id}
            </code>
          )}
          {profile.id && (
            <button
              className="text-button"
              disabled={busy}
              onClick={() =>
                action(async () => {
                  await api("/launch-profiles/" + profile.id, "DELETE");
                  setProfile({
                    id: "",
                    name: "",
                    installationId: "legacy",
                    groupKeys: [],
                    arguments: [],
                  });
                  setProfileArgs("");
                })
              }
            >
              실행 프로필 삭제
            </button>
          )}
        </div>
      </section>
      <section className="card">
        <div className="card-head">
          <div>
            <h2>이전 Ryujinx 설정 가져오기</h2>
            <p>이전 백엔드를 종료하세요. 기존 원격 이력은 그대로 보존됩니다.</p>
          </div>
        </div>
        <div className="card-body">
          <label className="field">
            <span>이전 상태 폴더 · state.db가 있는 폴더</span>
            <input
              value={sourceRoot}
              onChange={(e) => setSourceRoot(e.target.value)}
            />
          </label>
          <button
            disabled={
              busy ||
              dirty ||
              !sourceRoot ||
              bindings.length > 0 ||
              settings.installations.length > 0
            }
            onClick={() =>
              action(async () => {
                await api("/import/legacy", "POST", { sourceRoot });
                await imported();
              })
            }
          >
            설정·연결 가져오기
          </button>
          <p className="hint">
            비어 있는 새 상태 폴더에서 사용합니다. 이전 DB는 SQLite 백업으로
            보존합니다. baseline과 이전 미전송 작업은 가져오지 않습니다.
          </p>
          <button
            className="secondary"
            disabled={busy || dirty || settings.protocolVersion !== 2}
            onClick={() =>
              action(async () =>
                setLegacy(await api<Revision[]>("/import/legacy/catalog")),
              )
            }
          >
            이전 v1 서버 목록 조회
          </button>
          {legacy?.map((r) => {
            const binding = bindings.find((b) => b.remoteKey === r.saveKey);
            return (
              <div className="binding-row" key={r.id}>
                <div>
                  <h3>{r.title}</h3>
                  <small>
                    {r.slot} · {r.deviceName} · {r.id.slice(0, 8)}
                  </small>
                </div>
                <button
                  disabled={busy || !binding}
                  onClick={() =>
                    action(async () => {
                      await api("/import/legacy/revision", "POST", {
                        bindingId: binding!.id,
                        revisionId: r.id,
                      });
                      setLegacy(null);
                    })
                  }
                >
                  빈 v2 그룹으로 가져오기
                </button>
              </div>
            );
          })}
          {legacy?.length === 0 && <p>이전 서버에 현재 버전이 없습니다.</p>}
        </div>
      </section>
    </>
  );
}
