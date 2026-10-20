# RyuSync

Windows 11과 SteamOS에서 Ryujinx User Save를 WebDAV로 동기화하는 로컬 애플리케이션입니다. C#/.NET 백엔드가 파일·버전·작업을 관리하고, React 설정 페이지를 같은 로컬 서버에서 제공합니다.

**개발 초기 버전입니다.** Linux에서 합성 세이브와 테스트 WebDAV 서버를 사용한 자동 검증, 설정 화면 렌더링, Linux 실행 및 Windows 크로스 빌드를 확인했습니다. 실제 Windows·SteamOS의 Ryujinx와 NAS를 사용한 왕복 플레이 검증은 아직 수행하지 않았습니다. 아래 절차는 직접 확인하기 위한 사용·테스트 안내입니다.

## 목차

- [준비물과 배포 방식](#준비물과-배포-방식)
- [Windows 11에서 빌드하고 테스트하기](#windows-11에서-빌드하고-테스트하기)
- [SteamOS에서 사용하기](#steamos에서-사용하기)
- [웹 설정과 게임 등록](#웹-설정과-게임-등록)
- [Steam에 비 Steam 게임으로 등록하기](#steam에-비-steam-게임으로-등록하기)
- [Windows ↔ SteamOS 왕복 테스트](#windows--steamos-왕복-테스트)
- [충돌·실패·서버에만 있는 세이브 처리](#충돌실패서버에만-있는-세이브-처리)
- [저장 위치·업데이트·문제 해결](#저장-위치업데이트문제-해결)
- [자동 테스트 및 현재 제한](#자동-테스트-및-현재-제한)

## 준비물과 배포 방식

| 목적 | 필요한 것 |
|---|---|
| Windows에서 소스 빌드·자동 테스트 | Git, **.NET 10 SDK**, Node.js **22.12 이상인 22.x 또는 24.x**, npm |
| 빌드된 Windows 프로그램 실행 | Windows 11 x64, 배포 폴더 전체 |
| 빌드된 Linux 프로그램 실행 | SteamOS x64, 배포 폴더 전체, Desktop Mode의 브라우저 |
| 실제 파일 동기화 테스트 | 읽기·쓰기 가능한 WebDAV 서버, 계정, HTTPS 주소 |
| 실제 게임 로드 확인 | 각 기기의 Ryujinx와 게임, 가능하면 같은 게임 업데이트 버전 |

설치 프로그램은 [Git for Windows](https://git-scm.com/downloads/win), [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), [Node.js](https://nodejs.org/en/download) 공식 사이트에서 받으세요. .NET은 Runtime이 아닌 **SDK**를 선택해야 빌드와 테스트가 가능합니다.

현재 GitHub 저장소에는 소스만 올려 두었습니다. 이전 개발 환경의 `artifacts` 폴더나 실행 파일은 Git에 포함되어 있지 않으므로, 저장소를 복제한 뒤 아래 명령으로 빌드해야 합니다. GitHub의 소스 ZIP만 내려받아도 실행 파일이 바로 생기지는 않습니다.

`--self-contained true`로 만든 배포 폴더에는 .NET 런타임이 포함됩니다. **대상 기기에 .NET SDK나 Node.js를 설치하지 않고도 실행할 수 있습니다.** 다만 Linux의 기본 시스템 라이브러리까지 포함하는 것은 아닙니다. [Microsoft 배포 문서](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-publish)

### Linux 개발 PC에 .NET SDK 설치하기

Linux에서 소스를 빌드할 때도 **.NET 10 SDK**가 필요합니다. `dotnet: command not found`가 나오면 SDK 설치와 PATH를 확인하세요. 셸이 제안하는 `dotnet-host` 패키지만으로는 이 프로젝트를 빌드할 수 없습니다.

패키지 관리자 대신 [Microsoft 공식 설치 스크립트](https://learn.microsoft.com/en-us/dotnet/core/install/linux-scripted-manual)를 사용해 사용자 홈에 설치하는 예시입니다.

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet" --no-path
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
dotnet --list-sdks
dotnet --version
```

`10.0.xxx` SDK가 표시되는지 확인하세요. 설치 위치는 영구적인 `~/.dotnet`이며 `/tmp`에는 설치 스크립트만 둡니다. 새 Bash 터미널에서도 사용하려면 위의 `DOTNET_ROOT`와 `PATH` 설정을 `~/.bashrc`에 추가하세요. 로그인 셸에는 `~/.profile`에도 같은 설정을 둘 수 있습니다. 이미 추가했다면 반복해서 넣을 필요는 없습니다.

현재 열려 있는 터미널에는 설정 변경이 자동 반영되지 않으므로 `source ~/.bashrc`를 실행하거나 새 터미널을 여세요. 프론트엔드 빌드에는 Node.js와 npm도 필요합니다.

## Windows 11에서 빌드하고 테스트하기

### 1. 소스 준비

PowerShell을 열고 프로젝트를 둘 위치에서 실행합니다. 아래의 `C:\src`는 예시입니다.

```powershell
git clone https://github.com/tugsis/ryujinx-save-sync.git
Set-Location .\ryujinx-save-sync

dotnet --version
node --version
npm --version
```

설치 직후 명령을 찾지 못하면 PowerShell을 새로 열어 보세요. 기존에 복제한 저장소는 작업 내용을 확인한 뒤 `git pull --ff-only`로 갱신하면 됩니다.

### 2. 화면·백엔드 빌드 및 자동 테스트

**저장소 루트**에서 아래 순서대로 실행합니다. 단계가 실패하면 오류를 해결한 뒤 다음 단계로 진행하세요.

```powershell
npm --prefix frontend ci
npm --prefix frontend run build
dotnet build .\backend\RyuSync.Api
dotnet test .\backend\RyuSync.Tests --logger "console;verbosity=normal"
```

- 마지막 명령이 `Failed: 0` 또는 모든 테스트 통과로 끝나는지 확인합니다.
- 자동 테스트는 임시 데이터와 로컬 테스트 HTTP 서버를 사용합니다. 실제 WebDAV 계정이나 Ryujinx 설치는 필요하지 않습니다.
- Linux에서 실행한 테스트 통과 결과와 본인의 Windows에서 실행한 결과는 구분해서 확인하세요.

### 3. Windows 실행 폴더 만들기

```powershell
dotnet publish .\backend\RyuSync.Api -c Release -r win-x64 --self-contained true -o .\artifacts\win-x64
```

실행 파일은 `artifacts\win-x64\ryusync.exe`입니다. `.exe` 하나만 옮기지 말고 **`win-x64` 폴더 전체**를 보관·복사하세요. DLL, SQLite 라이브러리, `wwwroot` 화면 파일이 함께 필요합니다.

### 4. 테스트용 Ryujinx 데이터 복제

Ryujinx를 완전히 종료하고 원본 데이터를 별도 백업하세요. 원본 진행 상황을 보호하려면 처음에는 데이터 루트 전체를 복사한 테스트 설치본을 사용합니다.

일반 설치의 `%APPDATA%\Ryujinx`를 복사하는 PowerShell 예시:

```powershell
$ryuSource = Join-Path $env:APPDATA "Ryujinx"
$ryuTestRoot = Join-Path $env:USERPROFILE ("RyuSync-Test-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Path $ryuTestRoot | Out-Null
Copy-Item -LiteralPath $ryuSource -Destination (Join-Path $ryuTestRoot "Ryujinx") -Recurse
Write-Output (Join-Path $ryuTestRoot "Ryujinx")
```

포터블·커스텀 설치라면 `$ryuSource`를 **실제 데이터 루트**로 바꾸세요. 복사한 폴더의 `bis`, `games`, `system`을 확인합니다. RyuSync 웹 설정에도 출력된 테스트 데이터 루트를 입력해야 합니다.

**중요:** RyuSync의 데이터 폴더를 바꾸는 것만으로 Ryujinx의 데이터 폴더가 바뀌지는 않습니다. 테스트용 게임 플레이도 복사한 데이터 루트를 사용하도록 Ryujinx를 설정하세요. `-r`을 지원하는 빌드는 웹 설정의 실행 인수를 아래처럼 **두 줄로** 입력할 수 있습니다.

```text
-r
C:\Users\사용자\RyuSync-Test-날짜\Ryujinx
```

실제 출력된 경로로 바꾸세요. 인수는 한 줄씩 그대로 전달하므로 웹 입력란에서는 경로에 공백이 있어도 추가 따옴표를 넣지 않습니다. 해당 Ryujinx 빌드의 커스텀 데이터 루트 옵션을 확인한 후 테스트하세요.

### 5. 테스트 전용 상태로 실행

저장소 루트의 PowerShell에서 실행합니다.

```powershell
.\artifacts\win-x64\ryusync.exe serve --data-dir .\data\windows-test
```

콘솔에 표시된 `http://127.0.0.1:17843/#token=...` **전체 링크**를 브라우저에서 여세요. 이 PowerShell 창은 백엔드가 실행되는 동안 열어 둡니다. 브라우저를 닫아도 백엔드는 실행되며, 종료할 때는 콘솔에서 `Ctrl+C`를 누릅니다.

링크의 토큰은 일회성이며 유효 시간이 있습니다. 링크를 이미 사용했거나 다시 설정 화면을 열고 싶다면 **저장소 루트의 다른 PowerShell 창**에서 다음을 실행하세요.

```powershell
.\artifacts\win-x64\ryusync.exe open --data-dir .\data\windows-test
```

`open`은 같은 상태 폴더의 백엔드를 찾아 새 설정 링크를 엽니다. `serve`를 중복 실행하지 마세요. 테스트 중에는 `serve`, `open`, `launch` 모두 같은 `--data-dir`을 사용해야 합니다.

### 6. 실제 동작 확인 순서

1. [웹 설정](#웹-설정과-게임-등록)에서 테스트 데이터 루트와 별도의 WebDAV 공간(예: `ryusync-test`)을 등록합니다.
2. 게임 이름·Title ID·세이브 경로가 올바르게 탐색되는지 확인합니다.
3. 게임 하나만 등록해 **지금 동기화**를 실행하고 작업 기록에서 완료를 확인합니다.
4. 테스트용 Ryujinx 실행 파일·작업 디렉터리·인수를 설정합니다.
5. 대시보드의 **동기화 후 Ryujinx 실행**을 눌러 실행 전 동기화와 게임 실행을 확인합니다.
6. 플레이하고 저장한 뒤 **Ryujinx 프로그램 자체를 종료**합니다. 작업 기록에 `steam-post` 출처의 종료 후 동기화가 완료되는지 확인합니다.

명령으로 같은 실행 흐름을 확인할 수도 있습니다.

```powershell
.\artifacts\win-x64\ryusync.exe launch --data-dir .\data\windows-test
```

서버 주소·비밀번호·게임 등록이 준비되지 않았거나 충돌이 있으면 실행 전 단계에서 보류되는 것이 현재 동작입니다. 테스트 세이브로 정상 동작을 확인한 뒤 실제 데이터를 연결하세요.

## SteamOS에서 사용하기

### 1. Windows에서 Linux 배포 폴더 만들기

SteamOS 시스템에 SDK를 설치하는 대신, 위에서 준비한 **Windows 저장소 루트**에서 Linux용 배포 폴더를 만들 수 있습니다. 프론트엔드를 변경했다면 먼저 다시 빌드합니다.

```powershell
npm --prefix frontend run build
dotnet publish .\backend\RyuSync.Api -c Release -r linux-x64 --self-contained true -o .\artifacts\linux-x64
```

`artifacts\linux-x64` 전체를 USB·네트워크 전송 등으로 Steam Deck에 복사하세요. Windows용 `win-x64` 폴더와 구분해야 합니다. 필요하면 전송용 ZIP을 만듭니다.

```powershell
Compress-Archive -Path .\artifacts\linux-x64\* -DestinationPath .\artifacts\ryusync-linux-x64.zip -Force
```

### 2. Desktop Mode에서 배치·실행

SteamOS의 Desktop Mode로 전환하고 압축을 풀어 다음처럼 배치합니다. 사용자 이름이 `deck`인 일반적인 환경의 예시이며 실제 홈 경로에 맞춰 조정하세요.

```text
/home/deck/Applications/RyuSync/
  ryusync
  ryusync.dll
  libhostfxr.so
  ...다른 런타임·라이브러리 파일...
  wwwroot/
```

Konsole에서 실행합니다.

```bash
cd "$HOME/Applications/RyuSync"
chmod +x ./ryusync
./ryusync serve
```

콘솔의 전체 설정 링크를 Desktop Mode의 브라우저에서 엽니다. 다른 Konsole 창에서 화면을 다시 열려면:

```bash
cd "$HOME/Applications/RyuSync"
./ryusync open
```

이 절차에는 시스템 파티션 변경이나 SteamOS 읽기 전용 해제가 필요하지 않습니다. 배포 파일은 사용자 홈 아래에 둡니다. `Permission denied`면 실행 권한과 저장 장치의 실행 제한을 확인하세요. 내장 홈 디렉터리에서 먼저 테스트하는 편이 간단합니다.

### 3. SteamOS의 Ryujinx 연결

- 웹 설정의 데이터 루트 후보는 `~/.config/Ryujinx`지만, 입력란에는 `/home/deck/.config/Ryujinx`처럼 **절대 경로**를 넣으세요. `~`나 환경변수 문자열을 웹 UI가 확장하지 않습니다.
- EmuDeck·포터블·SD 카드 설치는 실제 경로가 다를 수 있습니다. Ryujinx에서 데이터 폴더를 확인하고 `bis`, `games`, `system`이 있는 루트를 선택하세요.
- Windows에서 사용한 것과 같은 WebDAV 주소·계정·공간 이름을 연결합니다. 게임과 사용자에 대응하는 **프로필 연결 이름**도 같게 지정합니다.
- 실행 파일에는 실제 Linux Ryujinx 네이티브 실행 파일을 지정하고, 작업 디렉터리와 필요 인수를 설정합니다.
- `flatpak run ...` 또는 셸 실행 문자열을 실행 파일 입력란에 통째로 넣지 마세요. 현재 구현은 분리 실행 래퍼·Flatpak·일부 AppImage의 실제 종료 추적을 검증하지 않았습니다.

처음에는 Desktop Mode에서 **지금 동기화 → 동기화 후 실행 → 종료 후 동기화**까지 확인한 다음 Gaming Mode를 테스트하세요.

## 웹 설정과 게임 등록

두 OS에서 같은 화면을 사용합니다. 현재 실제 저장소 연결은 **WebDAV**이며 Google Drive는 미구현입니다.

| 화면의 항목 | 설정 방법 |
|---|---|
| 기기 이름 | `Windows PC`, `Steam Deck`처럼 구분되는 이름 |
| WebDAV 서버 주소 | 서버의 실제 WebDAV HTTPS 엔드포인트. 일반 파일 관리 웹페이지 주소와 구분 |
| 사용자 이름·비밀번호 | 폴더 생성·목록·업로드·다운로드·테스트 파일 삭제 권한이 있는 계정 |
| 동기화 공간 이름 | 두 기기에 동일하게 입력. 예: 테스트는 `ryusync-test`, 실제 사용은 별도 공간 |
| HTTP 허용 | HTTPS가 없는 의도적인 테스트 서버에서만 선택. 계정 정보가 암호화되지 않음 |
| Ryujinx 데이터 폴더 | `bis/user/save` 자체가 아니라 `bis`, `games`, `system`의 상위 데이터 루트 |
| 실행 파일 | 실제 Ryujinx 실행 파일 하나의 절대 경로 |
| 작업 디렉터리 | 보통 Ryujinx 실행 파일이 있는 폴더. 비우면 실행 파일 폴더 사용 |
| 실행 인수 | 인수 하나당 한 줄. 데이터 루트 옵션·경로도 각각 다른 줄에 입력 |

설정 순서:

1. 저장소·데이터 루트를 입력하고 **설정 저장**을 누릅니다.
2. 비밀번호를 입력하고 **저장 후 연결 테스트**를 실행합니다. 전용 파일로 생성·전송·검증·삭제를 수행합니다.
3. Ryujinx를 완전히 종료한 상태에서 **게임 탐색**을 실행합니다.
4. `내 게임`에서 게임·프로필·저장 위치를 확인합니다.
5. 같은 사람의 두 기기에는 같은 프로필 연결 이름(예: `main`)을 사용해 게임을 등록합니다. 여러 사람이 있으면 `main`, `player2`처럼 구분하세요. 이 이름은 사용자가 확인하는 기기 간 대응 관계입니다.
6. **전체 동기화** 또는 게임별 **지금 동기화**를 실행하고 작업 기록을 확인합니다.

서버에는 `ryusync/<공간 이름>/saves/<게임·프로필 키>/` 아래에 버전별 아카이브와 Manifest가 생깁니다. UI에 등록한 User Save 내용만 전송하며 ROM·키·펌웨어·전체 데이터 루트를 업로드하지 않습니다.

비밀번호는 **현재 백엔드 프로세스의 메모리에만** 보관합니다. 백엔드를 재시작하면 웹에서 다시 입력해야 합니다. 특히 재부팅 후 Steam에서 바로 실행하면 인증 미설정 때문에 실행 전 동기화가 보류될 수 있습니다. 먼저 `open`으로 설정 화면을 열고 비밀번호를 입력하세요.

자동화 환경에서는 `RYUSYNC_WEBDAV_PASSWORD` 환경변수를 사용할 수 있습니다. OS 자격 증명 영구 보관은 후속 기능이며, 비밀번호를 Steam 실행 옵션·스크립트·Git에 적어 두는 방식은 권장하지 않습니다.

## Steam에 비 Steam 게임으로 등록하기

Steam의 **게임 → 라이브러리에 비 Steam 게임 추가**에서 RyuSync 실행 파일을 선택하고, 속성에서 아래처럼 설정합니다. [Valve 안내](https://help.steampowered.com/en/faqs/view/4B8B-9697-2338-40EC)

### Windows 11 예시

RyuSync 배포 폴더를 `C:\Tools\RyuSync`로 복사했다고 가정합니다. 설정 화면에서 실제 Ryujinx 실행 경로는 별도로 지정합니다.

| Steam 속성 | 값 |
|---|---|
| 이름 | `Ryujinx (RyuSync)` 등 자유롭게 지정 |
| 대상 | `"C:\Tools\RyuSync\ryusync.exe"` |
| 시작 위치 | `"C:\Tools\RyuSync"` |
| 실행 옵션 | `launch` |

앞서 만든 **테스트 상태 폴더**를 계속 사용할 때는 실행 옵션에 해당 폴더의 절대 경로도 넣습니다.

```text
launch --data-dir "C:\src\ryujinx-save-sync\data\windows-test"
```

이 예시 경로는 실제 저장소 위치로 바꾸세요. `--data-dir`을 생략하면 `%LOCALAPPDATA%\RyuSync`의 일반 설정을 사용하므로 테스트 설정과 섞이지 않습니다.

### SteamOS 예시

Desktop Mode의 Steam에서 비 Steam 게임으로 추가한 뒤 확인합니다. [Valve Desktop Mode 안내](https://help.steampowered.com/en/faqs/view/671A-4453-E8D2-323C)

| Steam 속성 | 값 |
|---|---|
| 이름 | `Ryujinx (RyuSync)` 등 자유롭게 지정 |
| 대상 | `"/home/deck/Applications/RyuSync/ryusync"` |
| 시작 위치 | `"/home/deck/Applications/RyuSync"` |
| 실행 옵션 | `launch` |
| 호환성 | Linux 네이티브 빌드이므로 Proton 강제 사용을 해제하고 실행 |

`/home/deck`은 실제 사용자 홈 경로에 맞춰 바꾸세요. 설정 후 Gaming Mode 라이브러리의 비 Steam 항목에서 실행합니다. 현재 Gaming Mode 전용 컨트롤러 창이 없으므로 충돌·인증 문제는 Desktop Mode 브라우저에서 해결해야 할 수 있습니다. 모드 전환으로 백엔드가 종료되면 메모리의 비밀번호도 사라지므로 Desktop Mode에서 입력한 비밀번호가 전환 후에도 유지된다고 가정하지 마세요.

### 실행 중·종료 후의 동작

```text
Steam에서 플레이
 → 서버와 로컬 버전 비교·동기화
 → 정상 완료되면 Ryujinx 실행
 → Ryujinx 프로세스 종료 대기
 → 세이브 안정화 및 종료 후 동기화
```

- Ryujinx 하나를 등록한 경우, 등록된 게임·프로필 전체를 시작 전에 비교하고 종료 후 변경된 항목을 전송합니다.
- **게임에서 메뉴로 돌아온 것만으로는 종료가 아닙니다.** Ryujinx 프로그램 자체를 종료해야 합니다.
- 플레이 중 웹에서 누른 동기화는 `종료 후 대기`로 표시하고 종료 후 전체 작업에 포함합니다.
- 정상 종료 뒤에도 작업 기록에서 `steam-post`가 완료되었는지 확인한 후 다음 기기에서 플레이하세요.
- Steam의 강제 중지, 백엔드 종료, 즉시 절전·전원 종료는 마지막 전송을 끊을 수 있습니다. 다음 실행에 상태를 확인하고 웹에서 다시 동기화하세요.
- 기존에 등록한 Ryujinx 바로가기를 직접 실행하면 시작 전 동기화 흐름을 거치지 않습니다. 동기화하려는 플레이에는 RyuSync 바로가기를 사용하세요.

## Windows ↔ SteamOS 왕복 테스트

가능하면 두 기기에서 같은 게임·업데이트 버전과 복제한 테스트 데이터를 사용합니다. 테스트용 원격 공간은 실제 사용 공간과 구분하세요.

| 순서 | 실행 | 확인할 결과 |
|---|---|---|
| 1 | Windows에서 게임 하나를 등록하고 동기화 | 원본 세이브 업로드 완료 |
| 2 | SteamOS에서 같은 게임·프로필 연결 이름을 등록하고 동기화 | 최초 내용이 다르면 버전 선택, Windows 원본을 선택해 적용 |
| 3 | SteamOS에서 게임 실행 | Windows에서 저장한 진행 상황이 실제 게임에서 로드됨 |
| 4 | SteamOS에서 진행·저장하고 Ryujinx 종료 | `steam-post` 업로드 완료 |
| 5 | Windows에서 Steam의 RyuSync 바로가기 실행 | `steam-pre` 완료 후 게임 시작, SteamOS에서 저장한 진행 상황 로드 |
| 6 | Windows에서 다시 진행·저장·종료 | 업로드 완료 후 SteamOS에서도 같은 진행 상황 확인 |

다운로드 적용이 있었다면 **백업** 화면에 이전 로컬 폴더가 기록되는지 확인하세요. 단순 전송 완료뿐 아니라 **게임에서 정상 로드되는지**까지 확인해야 해당 조합의 세이브 호환성을 검증할 수 있습니다.

## 충돌·실패·서버에만 있는 세이브 처리

### 두 기기에서 각각 플레이한 경우

공통 버전에서 두 기기가 각각 바뀌면 최신 시각만으로 덮어쓰지 않습니다. 웹 **작업 기록 → 버전 선택**에서 원하는 기기 버전을 선택합니다. 선택하지 않은 로컬 내용도 별도 원격 revision과 적용 전 백업으로 보존합니다. 두 세이브의 게임 내부 진행도를 자동 해석하거나 합치지는 않습니다.

처음 연결한 양쪽 내용이 다를 때도 같은 절차를 사용합니다. 새로 생성한 초기 세이브와 기존 서버 진행 상황이 다르면 서버 원본을 확인해 선택하세요.

### 서버에만 있고 로컬 세이브 폴더가 없는 경우

1. 대상 기기의 원하는 Ryujinx 프로필로 게임을 실행해 저장 위치를 생성하고 종료합니다.
2. RyuSync에서 다시 탐색해 같은 게임·프로필 연결 이름으로 등록합니다.
3. 수동 동기화를 요청합니다. 양쪽 내용이 다르면 **서버의 기존 버전**을 선택합니다.
4. 게임에서 정상 로드되는지 확인한 후 Steam 자동 실행 흐름을 사용합니다.

현재는 저장소 인덱스를 자동 생성하는 초기화 실행 모드가 없습니다. 원격 목록에 보인다고 로컬에 숫자 폴더를 임의로 만들거나 다른 기기의 내부 인덱스를 복사하지 마세요.

### 연결 실패·인증 만료·전송 중단

- 실행 전 실패: Ryujinx 실행을 보류하고 설정 화면에서 확인하도록 합니다. 현재 오프라인 실행 우회 버튼은 없습니다.
- 업로드 실패: 현재 파일과 미전송 스냅샷을 보존합니다. 연결·비밀번호를 복구한 뒤 **전체 동기화**를 눌러 재시도합니다.
- 재시작 후: 비밀번호를 다시 입력해야 합니다. 다음 동기화 요청에서 같은 저장소 범위의 미전송 스냅샷을 먼저 다시 게시합니다.
- 파일 교체 중 종료: 저널과 복사본을 보존하고 동기화를 차단할 수 있습니다. [복구 절차](docs/recovery.md)를 확인하세요.

## 저장 위치·업데이트·문제 해결

### 앱 데이터와 백업

| OS | 기본 상태 폴더 |
|---|---|
| Windows | `%LOCALAPPDATA%\RyuSync` |
| SteamOS | `$XDG_STATE_HOME/ryusync`, 미설정 시 `~/.local/state/ryusync` |
| 테스트·커스텀 | `--data-dir` 또는 `RYUSYNC_DATA_DIR`로 지정한 폴더 |

상태 폴더에는 `state.db`, `backups`, `staging`, 로컬 접속 정보 등이 들어갑니다. 세이브 디렉터리 안에 상태 폴더를 두지 마세요. 같은 상태 폴더는 한 백엔드만 실행할 수 있고, 서로 다른 에이전트가 같은 Ryujinx 데이터를 동시에 관리하게 설정하면 안 됩니다.

백업과 미전송 스냅샷은 자동 삭제하지 않습니다. 용량을 확인하고, 앱 종료 후에도 `staging`을 임의로 지우지 마세요. `instance.json`은 로컬 API 인증 정보를 포함하므로 외부에 공유하지 않습니다.

### 업데이트

1. Ryujinx와 RyuSync 백엔드를 종료합니다.
2. 소스를 갱신하고 프론트엔드 빌드·OS별 `dotnet publish`를 다시 실행합니다.
3. 별도의 새 배포 폴더로 전체 파일을 복사해 실행합니다. 기존 실행 중인 DLL만 교체하지 않습니다.
4. 기존 상태 폴더와 백업은 보존하고 동일한 `--data-dir` 설정을 유지합니다. 실행 파일 경로를 옮겼다면 Steam 대상도 수정합니다.

### 자주 발생하는 문제

| 증상 | 확인할 내용 |
|---|---|
| Linux에서 `dotnet: command not found` | .NET 10 SDK를 설치하고 `~/.dotnet`을 PATH에 추가. 현재 터미널은 `source ~/.bashrc`로 갱신 |
| `A compatible .NET SDK was not found` | `dotnet --list-sdks`에서 10.0 SDK 확인. 빌드에는 Runtime·Host 대신 SDK가 필요 |
| PowerShell이 `npm.ps1` 실행을 차단함 | `npm` 대신 `npm.cmd`로 같은 명령 실행. 예: `npm.cmd --prefix frontend ci` |
| GitHub에서 실행 파일을 찾을 수 없음 | 현재 소스 저장소이므로 OS별 publish를 먼저 실행 |
| 설정 링크에서 연결 필요·401 표시 | 같은 상태 폴더로 `open` 실행. 오래된 일회성 링크를 재사용하지 않음 |
| 빈 화면·화면 파일 없음 | `npm --prefix frontend run build` 후 다시 publish. 배포 폴더 전체를 복사했는지 확인 |
| 이미 실행 중·파일 잠금·포트 충돌 | 동일 상태 폴더의 백엔드 중복 실행 확인. 기존 프로세스를 확인한 뒤 정상 종료 |
| 다른 포트가 필요함 | `serve --port 17844` 사용. 이후 `open`·`launch`에도 같은 상태 폴더를 지정하면 실행 중 주소를 읽음. 자동 시작 시 사용할 포트도 `--port 17844`로 지정 |
| 세이브 인덱스 없음 | 데이터 루트 선택 확인. 게임을 한 번 실행했는지 확인 |
| 탐색 때 실행 중 오류 | Ryujinx를 완전히 종료하고 재탐색 |
| WebDAV 인증·권한·404·리디렉션 오류 | 최종 WebDAV 엔드포인트, 계정 권한, 폴더 경로 확인. 연결 테스트는 테스트 파일 삭제 권한도 필요 |
| HTTPS 인증서 또는 TLS 연결 오류 | 기기의 날짜·시간과 서버 인증서의 만료일·도메인·중간 인증서 확인. 만료된 인증서는 NAS 또는 리버스 프록시에서 갱신하고 WebDAV 주소에 실제 적용됐는지 확인한 뒤 연결 테스트 재시도 |
| WebDAV 도메인을 찾지 못함·접속 실패 | DNS 설정, 서버 주소·포트, 방화벽과 서버 실행 상태 확인 |
| SteamOS 실행 권한·라이브러리 오류 | Linux 배포 폴더 전체와 실행 권한 확인. 필요한 시스템 라이브러리 누락 여부 확인 |
| 온라인 디자인 초안에서는 실제 동기화가 안 됨 | `settings-preview` 및 기존 온라인 초안은 시뮬레이션. 실제 기능은 로컬 백엔드가 제공하는 화면에서 사용 |
| 다운로드 후 게임이 인식하지 못함 | 동일 게임·프로필·업데이트 버전, 실제 Ryujinx 데이터 루트와 실행 인수 확인. 백업을 보존하고 호환성 조사 |

설정 페이지는 해당 기기의 `127.0.0.1`에서만 제공합니다. NAS의 WebDAV 주소와 다르며, 다른 PC·휴대폰에서 이 주소로 원격 관리할 수는 없습니다.

## 자동 테스트 및 현재 제한

두 OS의 기본 자동 테스트·빌드:

```sh
npm --prefix frontend ci
npm --prefix frontend run build
dotnet build backend/RyuSync.Api
dotnet test backend/RyuSync.Tests
```

Linux 개발 환경의 실제 HTTP·WebDAV·실행 전후 통합 테스트:

```sh
python3 tests/integration.py
```

이 Python 테스트는 셸 기반 합성 실행 파일을 사용하므로 **Windows 네이티브 실행 대상으로 작성되지 않았습니다.** Windows에서는 .NET 테스트와 위의 실제 앱 확인 절차를 사용하세요. SteamOS에서 Python 테스트를 수행하려면 소스·Python·.NET SDK가 있는 개발 환경이 별도로 필요하며, 배포 폴더만 사용하면 실행할 수 없습니다.

GitHub Actions에는 Windows·Linux 빌드와 .NET 테스트, Linux Python 통합 테스트를 구성했습니다. 실행 결과는 저장소 [Actions](https://github.com/tugsis/ryujinx-save-sync/actions)에서 확인하세요. 특정 실행이 성공했다고 이 문서에서 가정하지 않습니다.

현재 남아 있는 항목:

- Google Drive, OS 자격 증명 영구 저장, 주기적 자동 동기화, Gaming Mode 전용 컨트롤러 화면.
- 실제 Windows·SteamOS의 Ryujinx·NAS 호환성 검증 및 분리 프로세스 실행 방식 지원.
- 자동 세이브 초기화와 오프라인 실행 선택.
- 백업 복원 버튼과 저널 복구 UI. 현재는 [수동 복구 안내](docs/recovery.md)를 제공합니다.

자동 탐색은 LibHac 0.19.0의 64바이트 키·값 인덱스 형식을 사용하며 알 수 없는 형식은 중지합니다. 백업·원격 이력 자동 정리도 아직 없습니다.

## 프로젝트 구조

```text
backend/RyuSync.Core/      탐색·스냅샷·버전·WebDAV·작업·SQLite
backend/RyuSync.Api/       로컬 API·정적 화면 제공·런처 CLI
backend/RyuSync.Tests/     안전성·충돌·전송 테스트
frontend/                 실제 백엔드에 연결한 React/TypeScript UI
settings-preview/         초기 디자인 초안 (시뮬레이션)
tests/integration.py      합성 데이터 기반 HTTP 통합 테스트
PLAN.md                   최종 목표 기획
```

[구현 현황](docs/implementation-status.md) · [개발·배포 상세](docs/development.md) · [복구 절차](docs/recovery.md) · [라이선스](LICENSE)
