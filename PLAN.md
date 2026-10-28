# M-Dir — Q-Dir 기능 복제 개발 계획

> 분석 대상: `D:\Q-Dir` (Q-Dir 12.77, Nenad Hrg / SoftwareOK.com, 2026-08-25 빌드)
> 작성일: 2026-10-28

---

## 1. 분석 결과

### 1.1 폴더 구성

| 파일 | 크기 | 역할 |
|---|---|---|
| `Q-Dir.exe` | 1.39 MB | 단일 실행 파일 (설치 프로그램·제거 프로그램·언어팩 모두 내장) |
| `Q-Dir.ini` | 10 KB | 전역 설정 (포터블 모드, 언어, 색상 필터, 열 상태) |
| `start.qdr` | 20 KB | 마지막 작업 공간(Workspace) 자동 저장본 (UTF-16 LE) |
| `Favoriten\Quick-Link\` | (빈 폴더) | 즐겨찾기 / 퀵링크 저장소 (`.lnk` 파일 기반) |

### 1.2 실행 파일 기술 구조

- **x86 32비트 Win32 GUI**, Visual C++ 6.0 링커, 순수 Win32 API + **ATL/WTL** (`WTL_XSplitterWindow`, `AXWIN Frame Window` 문자열 확인)
- 코드 서명: GlobalSign EV 인증서(유효)
- **파일 목록은 직접 그리지 않음** — Windows 셸의 `IShellView`(`SHELLDLL_DefView`) / `ExplorerBrowserControl`을 패널마다 호스팅함. 그래서 우클릭 메뉴, 썸네일, 열, 그룹화, 드래그앤드롭이 Windows 탐색기와 같음
- 주요 import:
  - `SHELL32`: `SHGetDesktopFolder`, `SHFileOperationW`, `SHGetFileInfoW`, `ShellExecuteExW`, `DragAcceptFiles`, `Shell_NotifyIconW`(트레이), `SHAppBarMessage`
  - `ole32`: `RegisterDragDrop`/`DoDragDrop`, `OleSetClipboard`/`OleGetClipboard`, `CoCreateInstance`
  - `gdiplus`: 이미지 미리보기·썸네일·회전
  - `comdlg32`/`WINSPOOL`: 인쇄, 페이지 설정, 색상 선택
  - `ADVAPI32`: 레지스트리(셸 메뉴 등록, 자동 시작, 제거 정보), `AdjustTokenPrivileges`(SeBackup/SeRestore 권한), `CryptHashData`
  - `COMCTL32`: ImageList, StatusBar, PropertySheet(설정 대화상자)
  - 동적 로드: `Magnification.dll`(돋보기), `Propsys.dll`, `UxTheme.dll`·`dwmapi.dll`(다크 모드), `Wtsapi32.dll`
- 내장 COM 컨트롤: `{8856F961-…}` WebBrowser(HTML/문서 미리보기), `{6BF52A52-…}` Windows Media Player(미디어 미리보기)
- 리소스: 메뉴 10개, 대화상자 57개, 단축키 테이블 5개, 아이콘 31개, PNG/BMP 다수
- **언어팩 40개**(`IMG/EN`, `IMG/KR`, `IMG/DE` …)가 자체 압축 형식으로 내장됨. 메뉴 텍스트는 숫자 ID(예: `10001`)이고 실행 시 언어팩 문자열로 바뀜

### 1.3 설정 파일 형식

**Q-Dir.ini** (INI, `WritePrivateProfileStringW`):
- `[Start]` 언어 ID, 창 위치(`WinRC`), 디자인 모드
- `[Program] portable_install=1`: 레지스트리 대신 exe 옆 INI 사용
- `[Filter2]` 색상 필터: `패턴=사용여부=RRGGBB=…`
  - 특수 키워드: `#Hidden`(숨김 파일), `#DIR`(폴더), `#BG`/`#BG-A`(배경)
  - 예: `*.zip;*.rar;*.7z` → `00AA00`(초록), `*.exe;*.dll;*.bat` → `FF0000`(빨강)
- `[Column_OS_10.0_Ploder1..4]` 패널별 열/정렬 상태: 셸 `IPropertyBag` 스트림을 `%XX`로 인코딩한 값

**start.qdr** (Workspace, UTF-16 INI 형식):
```
ViewMode=0                       ← 레이아웃 (1/2/3/4 패널 배치)
Folder1=<PIDL %XX 인코딩>&ViewModeFlags1=…&ViewMode1=4
SP0/SP1/SP2/SPT=5000              ← 분할선 위치 (1/10000 비율)
tree_mode / Tree1..4 / TreeNsplit ← 패널별 트리 표시·분할 위치
Vorschau / VorschauPos            ← 미리보기 창 표시·위치
Focus / colorthema
ColsAndSortN / ColsIDN            ← 패널별 열·정렬
windowplacement=x;y;w;h|sw=1
```
폴더 경로는 **PIDL**(ITEMIDLIST) 이진값으로 저장함. 그래서 "내 PC", 라이브러리 같은 가상 폴더도 저장됨.

### 1.4 기능 목록 (리소스·문자열·설정 키에서 확인)

| 영역 | 기능 |
|---|---|
| **레이아웃** | 1~4개 패널, 레이아웃 13종(가로/세로/3분할 변형/4분할), 분할선 드래그, 비율 저장 |
| **패널** | 패널마다 탭, 주소창(자동 완성·드롭다운·드라이브 목록), 툴바, 상태 표시줄, 필터 |
| **파일 목록** | 셸 기본 뷰: 큰/작은 아이콘, 목록, 자세히, 타일, 내용, 썸네일 크기(96/128/160/192/256). 열 사용자화, 열 너비 자동 맞춤, 그룹화, 행 전체 선택, 체크박스 선택 |
| **트리** | 패널별 트리 또는 공용 트리(`tree_mode`), 자동 펼침, 단일 펼침, 목록과 동기화, 색상 |
| **탭** | 새 탭/닫기(가운데 클릭·더블클릭), 여러 줄 탭, 탭 그룹, 탭 너비 모드, 툴팁, 기본 탭 |
| **탐색** | 뒤로/앞으로/위로, 패널 간 포커스 전환(Ctrl+Alt+1~4, F6), Ctrl+Tab 탭 전환 |
| **파일 작업** | 복사/잘라내기/붙여넣기/삭제/이름 바꾸기(셸 위임), 새 폴더, 하드 링크 생성, 이동 확인 |
| **즐겨찾기** | 즐겨찾기 폴더(.lnk), 퀵링크, 관리 대화상자(트리 드래그 정렬, 아이콘 변경), 시작 시 펼침 |
| **색상 필터** | 확장자·숨김·폴더별 글자색/배경색, 가져오기/내보내기/추천 |
| **파일 필터** | 패널별 포함 필터 + 제외(Negative) 필터(예: `Thumbs.db;*.bak`), 폴더 필터, 최근 필터 |
| **미리보기** | 이미지(GDI+), 미디어(WMP), HTML/문서(WebBrowser), 텍스트. 확대 및 회전 |
| **작업 공간** | `.qdr` 저장/불러오기(Ctrl+S / Ctrl+O), 시작 시 자동 복원, 새 작업 공간 |
| **폴더 크기** | 자세히 보기에 폴더 크기 열 계산(`fld_size`, 정렬 옵션) |
| **도구** | 다중 이름 바꾸기, 폴더 비교(빠른 비교/전문가 모드), Folder-to-Folder 복사, SHA256 해시, 이미지 중복 찾기(FSI 연동), 검색(MS-Search/Q-Search 베타) |
| **목록 내보내기·인쇄** | TXT(UTF-8/16), CSV, HTML, XLS 내보내기. 인쇄 미리보기, 출력 비율, 페이지 설정 |
| **경로 복사** | 경로·파일명·전체 경로 복사, 여기서 cmd/PowerShell/Explorer 열기 |
| **시스템 통합** | 폴더 우클릭 메뉴 "*Q-Dir"(`\shell\*Q-Dir\command`), 자동 시작(`Run` 키), 트레이 최소화(Ctrl/Shift+Esc), 전체 화면(F11) |
| **설치** | 포터블 ↔ 설치형, Quick Installer / Uninstaller 내장, 관리자 권한 실행(`-runas`) |
| **UI 사용자화** | 클래식/플랫 디자인, 다크 모드, 색상 테마, UI 확대(60~200%), 툴바 편집, 단축키 편집, 돋보기(Magnifier) |
| **다국어** | 40개 언어, 번역 편집기 내장 |
| **명령줄** | `.qdr` 파일 열기, 폴더 경로 인자, `-explore`, `-install`, `-uninstall`, `/Browse` |

### 1.5 확인된 기본 단축키

| 키 | 기능 | 키 | 기능 |
|---|---|---|---|
| F5 | 새로 고침 | Ctrl+N / O / S | 새 작업 공간 / 열기 / 저장 |
| F4 | 주소창 포커스 | Ctrl+P | 인쇄 |
| F6 / Shift+F6 | 다음/이전 패널 | Ctrl+Tab, Ctrl+←/→ | 탭 전환 |
| Ctrl+Alt+1~4 | 패널 1~4로 이동 | Alt+Back | 뒤로 |
| F11 / Alt+F11 | 전체 화면 / 메뉴 숨김 | F12 | 미리보기 토글 |
| Ctrl+Alt+A | 새 탭 | Alt+L | 레이아웃 선택 |
| Ctrl+R | 다중 이름 바꾸기 | Ctrl+Shift+V | 붙여넣기 관련 |

---

## 2. 기술 선택

### 권장안: C# (.NET 10) + WinForms + 셸 COM 인터롭

- 이 PC에는 **.NET SDK 10.0.400과 Git이 설치되어 있고, MSVC·CMake는 없습니다.** 추가 설치 없이 바로 시작할 수 있습니다.
- 핵심은 Q-Dir처럼 **`IExplorerBrowser`를 패널마다 호스팅**하는 것입니다. 파일 목록·우클릭 메뉴·썸네일·드래그앤드롭·열 관리를 Windows가 처리하므로 직접 구현할 양이 크게 줄어듭니다.
- WinForms는 Win32 HWND 기반이라 `IExplorerBrowser.Initialize(hwnd, …)`로 붙이기 쉽습니다(WPF보다 유리).
- COM 인터페이스는 `[GeneratedComInterface]`(.NET 8+ 소스 생성 COM)나 직접 작성한 `[ComImport]` 정의를 씁니다. `IExplorerBrowser`, `IExplorerBrowserEvents`, `IShellItem`, `IFolderView2`, `IShellView`, `IContextMenu`, `IFileOperation` 등이 필요합니다.
- 배포: `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` → 단일 exe(포터블)

### 대안: C++20 + Win32/WTL

원본과 같은 구조라 실행 파일이 작고 빠르지만, Visual Studio Build Tools를 설치해야 하고 개발 속도가 느립니다. 성능·용량이 최우선일 때만 선택하세요.

---

## 3. 단계별 개발 계획

각 단계는 끝날 때 **실행 가능한 상태**가 되도록 나눴습니다. 기간은 1인 개발 기준 대략치입니다.

### 0단계 — 프로젝트 골격 (1~2일)
- `git init`, `dotnet new winforms -n MDir`, 솔루션 구조:
  ```
  src/MDir/            WinForms 앱
    Shell/             COM 인터롭 (IExplorerBrowser, IShellItem, PIDL 유틸)
    Panes/             PaneControl, TabStrip, AddressBar
    Layout/            레이아웃 엔진, 스플리터
    Settings/          INI/Workspace 직렬화
    Features/          필터, 색상, 즐겨찾기, 미리보기, 도구
  tests/MDir.Tests/    xUnit (설정 직렬화, 필터 매칭, 레이아웃 계산)
  ```
- STA 스레드, `Application.SetHighDpiMode(PerMonitorV2)`, 앱 매니페스트(Common Controls v6, DPI aware)
- **완료 기준**: 빈 메인 창이 뜨고 빌드·테스트 파이프라인이 동작함

### 1단계 — 셸 브라우저 패널 1개 (3~5일) ★ 핵심 위험 요소
- `ExplorerBrowserHost : Control`: `CoCreateInstance(CLSID_ExplorerBrowser)` → `Initialize` → `BrowseToIDList`
- 크기 변경 시 `SetRect`. `IExplorerBrowserEvents`로 탐색 완료 이벤트를 받고, `IFolderView2`로 뷰 모드와 선택 항목을 가져옴
- `IServiceProvider`/`ICommDlgBrowser` 구현(포커스·더블클릭 처리), 키보드 메시지 전달(`TranslateAccelerator`)
- 뒤로/앞으로/위로(`BrowseToIDList` + `SBSP_NAVIGATEBACK` 등)
- **완료 기준**: 패널 하나에서 탐색기처럼 폴더 탐색, 우클릭 메뉴, 드래그앤드롭, 뷰 모드 전환이 됨

### 2단계 — 다중 패널 & 레이아웃 엔진 (3~4일)
- `PaneControl` = 주소창 + 툴바 + ExplorerBrowserHost + 상태 표시줄
- 레이아웃 엔진: 13종 레이아웃을 "분할 트리"(가로/세로 노드 + 비율)로 정의. 기본: 1, 2(좌우), 2(상하), 3×3종, 4분할
- 분할선은 비율(0~10000)로 저장(원본의 `SP0/SP1/SP2/SPT`와 같은 방식)
- 활성 패널 표시(테두리 강조), 패널 간 포커스 전환(F6, Ctrl+Alt+1~4)
- **완료 기준**: 레이아웃 메뉴로 1~4 패널을 바꿀 수 있고 분할선을 드래그할 수 있음

### 3단계 — 주소창 & 탭 (4~5일)
- 주소창: 경로 직접 입력(`SHParseDisplayName` → PIDL), `shell:` 경로 지원, 자동 완성(`IAutoComplete2` + `ACListISF`), 드라이브/특수 폴더 드롭다운, 선택 사항으로 브레드크럼
- 탭: 패널마다 TabStrip(직접 그리기). 새 탭/닫기/복제, 가운데 클릭 닫기, 드래그로 순서 변경, 여러 줄 모드, 툴팁
- 탭마다 탐색 기록 보관. 브라우저 인스턴스는 활성 탭만 유지하고 나머지는 PIDL과 뷰 상태만 저장(메모리 절약)
- **완료 기준**: 각 패널에서 여러 탭을 열고 주소를 직접 입력해 이동할 수 있음

### 4단계 — 설정 & 작업 공간 저장 (3일)
- `IniFile` 클래스(UTF-16, `GetPrivateProfileString` P/Invoke 또는 직접 파서)
- 포터블 모드: exe 옆에 `M-Dir.ini`가 있으면 사용하고, 없으면 `%APPDATA%\M-Dir`
- Workspace(`.mdr`) 형식: 레이아웃, 분할 비율, 패널·탭별 PIDL(`%XX` 인코딩), 뷰 모드, 트리 상태, 미리보기 상태, 창 위치
- 패널별 열·정렬 저장: `IFolderView2.GetSortColumns`/`IColumnManager`로 열 상태를 읽고 다시 적용
- 시작 시 `start.mdr` 자동 복원, 종료 시 저장. Ctrl+N/O/S
- (선택) **Q-Dir `.qdr` 가져오기**: 위 1.3의 형식대로 파싱해 PIDL과 레이아웃 변환
- **완료 기준**: 다시 시작하면 패널·탭·분할·창 위치가 그대로 복원됨

### 5단계 — 폴더 트리 (3~4일)
- `INameSpaceTreeControl`(Windows 탐색기 트리)을 호스팅. 패널별 트리 모드와 공용 트리 모드 지원
- 목록과 트리 동기화(목록에서 이동하면 트리 선택 이동, 트리를 클릭하면 패널 이동)
- 옵션: 자동 펼침, 단일 펼침, 행 전체 선택, 선 숨김, 들여쓰기
- **완료 기준**: 트리를 켜고 끌 수 있고, 목록과 양방향으로 동기화됨

### 6단계 — 색상 필터 & 파일 필터 (3~4일)
- **파일 필터**: `IFolderFilter`(`IFolderFilterSite.SetFilter`)로 포함/제외 와일드카드를 적용. 필터 입력 UI와 최근 필터 기록
- **색상 필터**: `ExplorerBrowser`의 목록은 셸 내부 ListView이므로 하위 클래스화한 `SysListView32`에서 `NM_CUSTOMDRAW`를 처리해 항목 텍스트 색을 바꿈. 항목명 → 규칙 매칭
  - 규칙 형식은 원본과 호환: `패턴;패턴=사용=RRGGBB`, 특수 키 `#Hidden`, `#DIR`
- 색상 규칙 편집 대화상자(가져오기/내보내기)
- **완료 기준**: `*.exe`가 빨간색으로 보이고, `*.bak`을 제외 필터로 숨길 수 있음
- ⚠ 위험: Windows 버전에 따라 DefView 내부 ListView 구조가 다름 → 1단계 직후 PoC로 먼저 검증

### 7단계 — 즐겨찾기 / 퀵링크 (2~3일)
- 즐겨찾기 폴더(`Favorites\`)에 `.lnk`를 저장(`IShellLink`). 하위 폴더는 메뉴 계층이 됨
- 메뉴·툴바 드롭다운으로 표시하고, Ctrl+클릭하면 새 탭에서 엶
- 관리 대화상자: 트리 뷰, 드래그 정렬, 이름 바꾸기, 아이콘 변경
- **완료 기준**: 현재 폴더를 즐겨찾기에 추가하고 메뉴에서 이동할 수 있음

### 8단계 — 미리보기 창 (3~4일)
- 1순위: 셸 미리보기 처리기(`IPreviewHandler`)를 호스팅 — Office, PDF, 텍스트, 이미지를 OS가 처리
- 대체 렌더러: 이미지(확대·회전), 텍스트(인코딩 감지), 미디어(`Windows.Media` 또는 WebView2 `<video>`)
- 선택이 바뀔 때 디바운스(150ms)와 비동기 로드
- **완료 기준**: F12로 미리보기를 켜고, 선택한 파일의 내용이 표시됨

### 9단계 — 메뉴, 툴바, 단축키, 파일 작업 (3~4일)
- 메인 메뉴(파일/편집/보기/즐겨찾기/도구/창/도움말)를 1.4 기능 목록에 맞춰 구성
- 명령 시스템: `Command` 레지스트리(ID → 실행 함수 + 활성화 조건) — 메뉴·툴바·단축키가 같은 명령을 공유
- 단축키 편집 가능(INI 저장), 기본값은 1.5 표
- 파일 작업은 `IFileOperation`(진행률·실행 취소를 셸이 처리), 다른 패널로 복사/이동("반대편 패널로")
- 경로 복사 3종, 여기서 cmd/PowerShell/탐색기 열기
- **완료 기준**: 모든 명령이 메뉴·툴바·단축키로 동작함

### 10단계 — 부가 도구 (5~7일, 우선순위별)
1. 폴더 크기 계산(백그라운드 스레드, 캐시, 상태 표시줄·열 표시)
2. 목록 내보내기(TXT/CSV/HTML/XLSX) 및 인쇄(미리보기 포함)
3. 다중 이름 바꾸기(패턴·번호·찾기/바꾸기, 미리보기 목록)
4. 폴더 비교(이름·크기·날짜, 빠른 비교 / 해시 비교)
5. Folder-to-Folder 복사/동기화
6. 파일 해시(SHA-256), 하드 링크 생성
7. 검색(Windows Search `search-ms:` 연동 + 간단한 자체 검색)

### 11단계 — 시스템 통합 & 사용자화 (3~4일)
- 트레이 최소화(`NotifyIcon`), 자동 시작, 전체 화면, 단일 인스턴스 옵션(Mutex + 명명된 파이프로 인자 전달)
- 탐색기 폴더 우클릭 메뉴 등록/해제(`HKCU\Software\Classes\Directory\shell\M-Dir`)
- 명령줄: 폴더 경로, `.mdr` 파일, `-explore`
- 다크 모드(`DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)`, `SetWindowTheme(hwnd,"DarkMode_Explorer")`), UI 확대 배율
- 툴바 편집 대화상자

### 12단계 — 다국어 & 배포 (2~3일)
- 문자열을 `.resx`(한국어/영어 우선)로 관리하고 런타임에 언어 전환
- 단일 exe로 self-contained publish, 포터블 zip, (선택) 설치 프로그램
- 회귀 테스트 체크리스트(아래 4장)로 검증

**총 예상: 약 8~11주** (핵심 MVP인 0~4단계: 약 3주)

---

## 4. 검증 체크리스트 (Q-Dir와 동작 비교)

- [ ] 4분할에서 각 패널이 독립적으로 탐색하고, 패널 간 드래그앤드롭 복사/이동이 됨
- [ ] 가상 폴더(내 PC, 휴지통, 네트워크, 라이브러리, zip 내부)를 탐색할 수 있음
- [ ] 다시 시작하면 레이아웃·탭·폴더·뷰 모드·열·정렬이 복원됨
- [ ] 색상 필터와 제외 필터가 모든 뷰 모드에서 적용됨
- [ ] 우클릭 메뉴가 탐색기와 같음(셸 확장 포함)
- [ ] 고DPI·다중 모니터·다크 모드에서 UI가 깨지지 않음
- [ ] 1만 개 이상 파일이 있는 폴더에서도 반응이 빠름
- [ ] 포터블 모드에서 레지스트리에 아무것도 쓰지 않음

## 5. 주요 위험 요소와 대응

| 위험 | 대응 |
|---|---|
| .NET에서 `IExplorerBrowser` 호스팅 시 포커스·키보드 문제 | 1단계에서 PoC로 먼저 검증. `IInputObject`, `TranslateAccelerator`를 전달하고, 실패하면 C++/CLI 브리지 또는 C++ 전환을 검토 |
| 셸 내부 ListView 색상 변경(비공개 구조 의존) | 6단계 PoC를 1단계 직후로 앞당김. 실패하면 색상 필터를 `IListView` 비공개 인터페이스나 오버레이 방식으로 대체 |
| 탭이 많을 때 메모리 사용 | 활성 탭만 브라우저 인스턴스를 유지하고 나머지는 지연 생성 |
| 폴더 크기 계산 부하 | 취소 가능한 백그라운드 작업, 결과 캐시, 네트워크 드라이브 제외 옵션 |
| 원본 상표·리소스 | 아이콘·문자열·언어팩·바이너리는 재사용하지 않고 기능만 독자적으로 구현. `.qdr` 가져오기는 형식 호환만 제공 |
