export interface Installation {
  gameListCacheRoot?: string;
  id: string;
  name: string;
  kind: "ryujinx" | "yuzu";
  build: string;
  dataRoot: string;
  nandRoot: string;
  executable: string;
  workingDirectory: string;
  arguments: string[];
}
export interface LaunchProfile {
  id: string;
  name: string;
  installationId: string;
  groupKeys: string[];
  arguments: string[];
}
export interface RestoreReservation {
  id: string;
  installationId: string;
  titleId: string;
  slot: string;
  revisionId: string;
  scope: string;
}
export interface Settings {
  protocolVersion: number;
  installations: Installation[];
  deviceId: string;
  deviceName: string;
  dataRoot: string;
  webDavUrl: string;
  webDavUser: string;
  workspace: string;
  allowInsecureHttp: boolean;
  executable: string;
  workingDirectory: string;
  arguments: string[];
  syncAfterLaunch: boolean;
}
export interface Save {
  thumbnailUrl?: string;
  titleSource?: string;
  installationId: string;
  emulatorKind: string;
  id: string;
  titleId: string;
  title: string;
  userId: string;
  profileName: string;
  saveDataId: string;
  saveRoot: string;
  contentPath: string;
  status: string;
}
export interface Binding {
  thumbnailUrl?: string;
  titleSource?: string;
  installationId: string;
  emulatorKind: string;
  emulatorBuild: string;
  gameVersion: string;
  id: string;
  titleId: string;
  title: string;
  userId: string;
  saveDataId: string;
  saveRoot: string;
  slot: string;
  enabled: boolean;
  remoteKey: string;
}
export interface Revision {
  sourceEmulator: string;
  sourceBuild: string;
  gameVersion: string;
  id: string;
  saveKey: string;
  parents: string[];
  titleId: string;
  title: string;
  slot: string;
  deviceName: string;
  createdAt: string;
  contentHash: string;
}
export interface Outcome {
  bindingId: string;
  status: string;
  message: string;
  localHash?: string;
  heads?: string[];
  versions?: Revision[];
}
export interface Job {
  id: string;
  kind: string;
  source: string;
  status: string;
  message: string;
  createdAt: string;
  results: Outcome[];
}
export interface Session {
  id: string;
  status: string;
  message: string;
  jobId?: string;
}
export interface Status {
  version: string;
  device: string;
  os: string;
  credentialUnlocked: boolean;
  launch?: Session;
  pendingUploads: number;
  recovery: number;
}
export interface Backup {
  id: string;
  bindingId: string;
  title: string;
  path: string;
  contentHash: string;
  createdAt: string;
}
export async function api<T>(
  path: string,
  method = "GET",
  body?: unknown,
): Promise<T> {
  const response = await fetch("/api" + path, {
    method,
    credentials: "same-origin",
    headers: body === undefined ? {} : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) {
    let message = `요청 실패 (${response.status})`;
    try {
      const json = await response.json();
      message = json.error || message;
    } catch {}
    throw new Error(message);
  }
  return response.status === 204 ? (undefined as T) : response.json();
}
