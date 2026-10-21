import { useState } from "react";
import { FieldRequirement } from "./FieldRequirement";
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
  scan: (installationId: string, saveFirst?: boolean) => Promise<void>;
  save: () => Promise<void>;
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
  save,
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
          <p className="hint">
            필수 항목과 탐색 경로를 입력하세요. 실행 파일은 Steam에서
            에뮬레이터를 실행할 때만 필요하며, 빌드·작업 디렉터리·인수는 비워도
            게임 탐색이 가능합니다.
          </p>
          {settings.installations.map((i) => {
            const scanBlocked = busy
              ? "작업 중입니다. 완료되면 게임을 탐색할 수 있습니다."
              : !i.name.trim()
                ? "필수 항목인 설치본 이름을 입력하세요."
                : !i.dataRoot.trim() &&
                    (i.kind !== "yuzu" || !i.nandRoot.trim())
                  ? i.kind === "yuzu"
                    ? "데이터 루트 또는 커스텀 NAND 루트 중 하나를 입력하세요."
                    : "데이터 루트를 입력하세요."
                  : "";
            return (
              <div className="installation-editor" key={i.id}>
                <div className="form-row">
                  <label className="field">
                    <span>
                      설치본 이름 <FieldRequirement value="필수" />
                    </span>
                    <input
                      required
                      value={i.name}
                      onChange={(e) => update(i.id, { name: e.target.value })}
                    />
                  </label>
                  <label className="field">
                    <span>
                      에뮬레이터 <FieldRequirement value="필수" />
                    </span>
                    <select
                      required
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
                  <span>
                    에뮬레이터 빌드 <FieldRequirement value="선택" />
                  </span>
                  <input
                    value={i.build}
                    onChange={(e) => update(i.id, { build: e.target.value })}
                    placeholder="예: 사용 중인 빌드 번호"
                  />
                  <small>
                    직접 확인한 빌드 번호입니다. Ryujinx↔Yuzu 교차 호환성
                    확인을 위해 입력을 권장합니다.
                  </small>
                </label>
                <label className="field">
                  <span>
                    데이터 루트{" "}
                    <FieldRequirement
                      value={
                        i.kind !== "yuzu"
                          ? "탐색 시 필수"
                          : i.nandRoot.trim()
                            ? "선택"
                            : i.dataRoot.trim()
                              ? "탐색 시 필수"
                              : "둘 중 하나 필수"
                      }
                    />
                  </span>
                  <input
                    required={i.kind !== "yuzu"}
                    list={"roots-" + i.id}
                    value={i.dataRoot}
                    onChange={(e) => update(i.id, { dataRoot: e.target.value })}
                  />
                  <datalist id={"roots-" + i.id}>
                    {candidateRoots[i.id]?.map((root) => (
                      <option key={root} value={root} />
                    ))}
                  </datalist>
                  <small>
                    {i.kind === "yuzu"
                      ? i.nandRoot.trim()
                        ? "현재는 아래 커스텀 NAND 경로를 사용합니다. 이 항목은 비워도 됩니다."
                        : "기본 NAND를 사용하면 이 경로만 입력하세요. 실제 탐색 위치는 데이터 루트/nand입니다."
                      : "bis·games·system 폴더가 들어 있는 Ryujinx 데이터 폴더입니다."}
                  </small>
                </label>
                {i.kind === "yuzu" && (
                  <label className="field">
                    <span>
                      커스텀 NAND 루트{" "}
                      <FieldRequirement
                        value={
                          i.dataRoot.trim()
                            ? "선택"
                            : i.nandRoot.trim()
                              ? "탐색 시 필수"
                              : "둘 중 하나 필수"
                        }
                      />
                    </span>
                    <input
                      value={i.nandRoot}
                      onChange={(e) =>
                        update(i.id, { nandRoot: e.target.value })
                      }
                    />
                    <small>
                      별도 NAND를 사용하면 이 경로만 입력해도 됩니다. 비우면 위
                      데이터 루트/nand를 사용합니다. 두 경로를 모두 입력할
                      필요는 없습니다.
                    </small>
                  </label>
                )}
                <label className="field">
                  <span>
                    실제 실행 파일{" "}
                    <FieldRequirement value="Steam 실행 시 필수" />
                  </span>
                  <input
                    value={i.executable}
                    onChange={(e) =>
                      update(i.id, { executable: e.target.value })
                    }
                  />
                  <small>
                    EXE 또는 직접 실행 가능한 Linux 바이너리입니다. 게임 탐색과
                    웹 동기화만 사용할 때는 비워도 됩니다.
                  </small>
                </label>
                <label className="field">
                  <span>
                    작업 디렉터리 <FieldRequirement value="선택" />
                  </span>
                  <input
                    value={i.workingDirectory}
                    onChange={(e) =>
                      update(i.id, { workingDirectory: e.target.value })
                    }
                  />
                  <small>비우면 실행 파일이 있는 폴더를 사용합니다.</small>
                </label>
                <label className="field">
                  <span>
                    기본 실행 인수 <FieldRequirement value="선택" />
                  </span>
                  <textarea
                    rows={3}
                    value={i.arguments.join("\n")}
                    onChange={(e) =>
                      update(i.id, { arguments: e.target.value.split("\n") })
                    }
                  />
                  <small>필요한 경우 한 줄에 인수 하나씩 입력하세요.</small>
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
                    disabled={!!scanBlocked}
                    aria-describedby={"scan-help-" + i.id}
                    onClick={() => action(() => scan(i.id, dirty))}
                  >
                    {dirty ? "저장 후 게임 탐색" : "게임 탐색"}
                  </button>
                  <button
                    className="secondary"
                    disabled={busy || !dirty}
                    onClick={() => action(save)}
                  >
                    설정 저장
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
                <p
                  id={"scan-help-" + i.id}
                  className="scan-guidance"
                  role="status"
                >
                  {scanBlocked ||
                    (dirty
                      ? "변경한 설정을 저장한 뒤 이 설치본의 게임·세이브를 탐색합니다."
                      : "설정이 저장되었습니다. 게임 탐색을 눌러 이 설치본의 게임·세이브를 찾으세요.")}
                  {!scanBlocked && " 탐색 전에 에뮬레이터를 완전히 종료하세요."}
                </p>
                <small className="hint">
                  연결된 게임이 있으면 종류·경로·빌드는 변경할 수 없습니다.
                  AppImage·Flatpak 자동 실행은 아직 지원하지 않으며 종료 후 웹
                  동기화는 가능합니다.
                </small>
              </div>
            );
          })}
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
            ‘저장 후 게임 탐색’으로 바로 진행하거나, ‘설정 저장’ 후 ‘게임
            탐색’을 누르세요. 세이브 내부의 프로필 ID를 바꾸거나 프로필 DB를
            복사하지 않습니다.
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
            <span>
              프로필 이름 <FieldRequirement value="필수" />
            </span>
            <input
              value={profile.name}
              onChange={(e) => setProfile({ ...profile, name: e.target.value })}
            />
          </label>
          <label className="field">
            <span>
              실행할 설치본 <FieldRequirement value="필수" />
            </span>
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
            <span>
              프로필 추가 인수 <FieldRequirement value="선택" />
            </span>
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
            <span>
              이전 상태 폴더 <FieldRequirement value="가져오기 시 필수" />
            </span>
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
