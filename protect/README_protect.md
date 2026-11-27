# 리버스엔지니어링 보호 실습 (ConfuserEx)

`test_rtsp` 를 예시로, .NET 앱을 난독화·보호하는 과정을 직접 돌려보는 안내서입니다.
핵심은 **"보호 전 → 보호 후"를 눈으로 비교**하는 것.

---

## 0. 준비물 (모두 무료)

- **ConfuserEx** : https://github.com/mkaring/ConfuserEx/releases
  - `ConfuserEx.zip` 받아서 아무 폴더에 압축 해제 (GUI: `ConfuserEx.exe`)
- **ILSpy** (복원 체험용) : https://github.com/icsharpcode/ILSpy/releases

> ⚠️ 받은 zip 은 각각 **빈 폴더에** 풀고, 그 안의 실행파일을 "탐색기에서" 실행하세요.

---

## 1. 보호 전 상태 확인 (왜 위험한지 체감)

1. 먼저 앱을 빌드해 `bin\Debug\test_rtsp.exe` 가 있게 합니다.
2. ILSpy 로 `test_rtsp.exe` 를 엽니다.
3. 왼쪽 트리에서 `test_rtsp > Services > RtspServer` 를 펼쳐 보세요.
   - `StartCoreAsync`, `BuildConfig` 같은 **메서드 이름이 그대로**
   - `"RTSP 포트 {0} 를 다른 프로그램이..."` 같은 **한글 문자열이 평문**
   - → **아무 보호도 없으면 사실상 소스 공개**임을 확인

---

## 2. ConfuserEx 로 보호하기

### GUI 방식 (쉬움)
1. `ConfuserEx.exe` 실행
2. **Project** 탭 → 좌측 하단 폴더 아이콘으로 이 폴더의 `test_rtsp.crproj` 열기
   - 또는 수동 설정: Base Directory = `bin\Debug`, 모듈에 `test_rtsp.exe` 추가
3. **Settings** 탭에서 적용된 보호(rename, ctrl flow, anti debug...) 확인
4. **Protect!** 탭 → `Protect!` 버튼 클릭
5. 완료되면 `bin\Protected\test_rtsp.exe` 생성됨

### 설정 요약 (`test_rtsp.crproj`)
| 보호 | 역할 | AI 상대 효과 |
|---|---|---|
| rename | 이름을 기호로 | 이름 기반 의미 추측 차단 |
| **ctrl flow** | 흐름을 뒤섞음 | **★ 로직 복원 방해 (핵심) ★** |
| constants | 문자열 암호화 | 평문 로그/URL 숨김 |
| anti debug | 디버거 감지 | dnSpy·동적 분석 차단 |
| anti tamper | 변조 감지 | 크랙 방지 |
| anti dump / anti ildasm | 덤프·역어셈블 방해 | 추출 난이도↑ |

---

## 3. 보호 후 다시 확인 (방어됐는지 체감)

1. ILSpy 로 이번엔 `bin\Protected\test_rtsp.exe` 를 엽니다.
2. 아까 보이던 `StartCoreAsync`, 한글 문자열이 **사라지거나 뒤죽박죽**인지 확인
3. → **"뜯는 비용이 확 올라갔다"** 를 체감. 이게 이 실습의 목표.

4. 보호된 exe 가 **정상 실행되는지 반드시 테스트** (난독화가 기능을 깨지 않았는지).
   - WPF 는 rename 이 가끔 XAML 참조를 깨뜨립니다.
   - 실행이 안 되면 `.crproj` 에서 `<protection id="rename" />` 한 줄을 지우고
     다시 Protect → 원인이 rename 인지 먼저 확인하세요.

---

## 4. 실전 적용 시 주의

- **배포용은 Release 로.** `.crproj` 의 `bin\Debug` → `bin\Release` 로 바꾸기.
  (Debug 는 최적화 off + pdb 포함이라 보호해도 더 잘 분석됨)
- **비밀값은 난독화로 못 지킴.** 문자열 암호화해도 실행 중 메모리엔 평문으로 뜸.
  → 애초에 키/비번을 클라이언트에 두지 않기. (이 프로젝트는 송출 계정을
    매 실행 랜덤 생성 → 좋은 예: `Services\RtspServer.cs` 의 `RandomToken`)
- **난독화 = 차단이 아니라 지연.** 목표는 "불가능하게"가 아니라
  "들인 가치보다 비싸게". AI 가 있으면 그 지연이 짧아지므로 ctrl flow 가 중요.

---

## 핵심 교훈 3줄
1. 클라이언트 코드는 100% 못 막는다 → **비용을 올리는 게 목표**.
2. **흐름 난독화(ctrl flow)** 가 AI 상대 가장 중요한 방어.
3. 진짜 비밀은 난독화가 아니라 **설계(클라이언트에 안 두기)** 로 지킨다.
