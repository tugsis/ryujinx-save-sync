# 개발 및 배포

## 빌드 순서

1. `npm --prefix frontend ci`
2. `npm --prefix frontend run build`
3. `dotnet build backend/RyuSync.Api`
4. `dotnet test backend/RyuSync.Tests`

프론트엔드 빌드 결과는 API 프로젝트 빌드 시 `wwwroot`로 복사됩니다. UI를 바꾸면 반드시 프론트엔드를 다시 빌드한 뒤 백엔드도 빌드하세요. 현재 웹 UI는 2초 간격으로 상태를 조회하며 SSE는 후속 작업입니다.

## 자가 포함 배포

```sh
dotnet publish backend/RyuSync.Api -c Release -r linux-x64 --self-contained true -o artifacts/linux-x64
dotnet publish backend/RyuSync.Api -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

각 배포 폴더 전체를 복사해야 합니다. SQLite 네이티브 라이브러리와 `wwwroot`, `LibHac.dll` 등이 함께 필요합니다. 현재 단일 파일 배포·설치 프로그램·코드 서명은 제공하지 않습니다.

## 상태 파일

- Windows: `%LOCALAPPDATA%\RyuSync`
- Linux: `$XDG_STATE_HOME/ryusync` 또는 `~/.local/state/ryusync`
- `--data-dir` 또는 `RYUSYNC_DATA_DIR`로 변경 가능. 실제 세이브 경로 안에 두지 마세요.

`state.db`는 SQLite WAL 모드로 설정·연결·기준 버전·작업·미전송 스냅샷·교체 저널을 보관합니다. `instance.json`은 실행 중인 로컬 API 인증 정보를 담으며 Git이나 외부 서버로 올리지 마세요. Linux에서는 상태 폴더를 사용자 전용 권한으로 만듭니다.

동일한 상태 폴더는 파일 잠금으로 중복 실행을 막습니다. 다른 상태 폴더를 실행할 때는 다른 포트도 지정하세요. 두 에이전트가 같은 Ryujinx 데이터를 관리하게 설정하지 마세요.

## 인덱스 읽기

LibHac 0.19.0의 공개 `SaveDataAttribute`와 `SaveDataIndexerValue` 구조를 이용해 IMKV 키·값을 읽습니다. 파일시스템을 마운트하거나 인덱스를 생성·수정하지 않습니다. Account 타입, User 공간, Normal 상태만 포함합니다.

참조:

- [LibHac 0.19.0 패키지](https://www.nuget.org/packages/LibHac/0.19.0)
- [Ryujinx 세이브 관리자 구현](https://git.axenov.dev/Museum/ryujinx/commit/d9053bbe3745846dd758561e24dd060d76b3ad9d)

실기 검증 전에는 특정 포크의 전체 호환성을 보장하지 않습니다. 합성 fixture의 통과는 바이너리 형식 처리와 안전성에 대한 검증이며 실제 게임의 호환성 검증과 구분합니다.

## 구현 경계

이 커밋은 초기 실행 가능한 구현입니다. PLAN.md 전체 MVP를 완료한 릴리스가 아닙니다. 다음 단계는 실제 Ryujinx 빌드와 저장 경로 확인, SteamOS/Windows 실기 테스트, 자격 증명 영구 저장, 안전한 복원 UI입니다.
