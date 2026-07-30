# 체커별 실수정 Diff 예시 코퍼스

이 폴더는 Sparrow Track A/B/C 보완을 위해 **체커별 실제 수정 커밋에서 최소 before/after 패턴만 추출한 자료**를 쌓는 곳이다.

목표는 폐쇄망/업무 원본 코드를 반출하지 않고, 이미 사람이 수정해 통과시킨 커밋의 핵심 diff를 익명화하여 다음 작업에 재사용하는 것이다.

- Track A/B CLI가 놓친 자동화 후보 발굴.
- Track A/B CLI guard 조건 보강.
- Track C LLM 판단 가이드의 before/after 예시 강화.
- 로컬/폐쇄망 LLM이 판단하기 어려운 케이스의 공통 패턴 축적.

## 입력 자료

가장 좋은 입력은 **체커 항목별로 분리된 git 커밋**이다.

권장 커밋 단위:

- 한 커밋 = 한 체커 또는 강하게 연관된 한 규칙군.
- 커밋 메시지에 체커 키 또는 체커명을 포함.
- 검토필요 자동수정은 커밋 메시지에 `검토필요` 또는 `review-needed` 포함.
- 업무 기능 변경과 Sparrow 대응 변경을 섞지 않는다.

예:

```text
sparrow(A): PRACTICE.OBVIOUS_VARIABLE_TYPE.NOT_USED_IMPLICIT_TYPING var 변환
sparrow(A)! 검토필요: foreach Cast<T> 기반 var 변환
sparrow(C): FORWARD_NULL null guard 추가
sparrow(C): RESOURCE_LEAK using 블록 전환
```

## 비식별화 원칙

폐쇄망 코드 또는 업무 로직이 드러나면 안 된다. diff를 그대로 옮기지 말고 **체커 해결에 필요한 최소 구조만 남긴다**.

반드시 익명화할 것:

- 파일 경로, 실제 파일명.
- 클래스명, 메서드명, 변수명, enum/member명.
- 문자열 리터럴, UI 문구, 로그 문구, 도메인 용어.
- 고객사/장비/프로토콜/업무 기능을 추정할 수 있는 이름.
- 전체 함수, 전체 클래스, 긴 연속 코드.

남겨도 되는 것:

- 제어 흐름 구조: `if`, `try/catch/finally`, `using`, `foreach`.
- 타입의 성격: `Stream`, `IDisposable`, `IList<T>`, `XmlNode`, `object`, `Exception`.
- 체커 해결에 필요한 최소 before/after 코드.
- 자동화 guard 판단에 필요한 타입/초기화식/소유권 구조.

## 작성 위치

체커 키별로 파일을 만든다.

```text
references/real-fix-patterns/<CHECKER_KEY>.md
```

체커 키가 Sparrow 공식 키와 1:1로 정해지지 않은 내부 규칙은 의미 있는 kebab/caps 이름을 사용한다.

예:

```text
references/real-fix-patterns/FORWARD_NULL.md
references/real-fix-patterns/RESOURCE_LEAK.md
references/real-fix-patterns/PRACTICE.OBVIOUS_VARIABLE_TYPE.NOT_USED_IMPLICIT_TYPING.md
references/real-fix-patterns/PRACTICE.LOOP_VARIABLE.NOT_USED_IMPLICIT_TYPING.md
```

## 문서 구조

각 체커 문서는 아래 구조를 따른다.

```md
# <CHECKER_KEY>

## 체커 목적

- 한 줄 요약.

## 실수정 패턴 1: <패턴명>

### Before

```csharp
// 익명화된 최소 코드
```

### After

```csharp
// 익명화된 최소 코드
```

### 추출 기준

- 원본 커밋: `<커밋 해시 또는 내부 추적 ID>`
- 원본 diff에서 보존한 구조:
- 제거/익명화한 정보:

### 자동화 판단

- CLI 자동화 가능: 예|아니오|조건부
- 적용 Track: A|B|C|공통
- 필요한 guard:
- 빌드/동작 보존 포인트:

### LLM 판단 포인트

- 결함 판단 기준:
- 보류해야 하는 경우:
- 추가 문맥이 필요한 경우:
```

## 커밋에서 패턴을 뽑는 지시문

LLM 또는 사람이 체커별 커밋을 보고 패턴을 정리할 때는 아래 지시문을 사용한다.

```text
아래 커밋은 Sparrow 체커별 수정 커밋이다.
커밋 diff를 읽고, 원본 업무 코드를 그대로 옮기지 말고
체커 해결에 필요한 최소 before/after 패턴만 익명화해서 정리해라.

목표:
- Track A/B CLI가 놓친 패턴 보완
- Track C LLM 판단 가이드의 예시 강화
- 폐쇄망 코드 반출 없이 일반화된 수정 패턴 축적

규칙:
1. 파일명, 클래스명, 메서드명, 변수명, 문자열 리터럴, 경로, 도메인 용어는 익명화한다.
2. 전체 함수/파일을 옮기지 말고 체커 해결에 필요한 최소 코드 조각만 남긴다.
3. before/after 형식으로 정리한다.
4. 해당 예시가 어떤 체커에 대응하는지 명시한다.
5. CLI 자동화 가능 여부와 LLM/사람 판단 필요 여부를 분류한다.
6. 자동화 가능이면 필요한 guard 조건을 적는다.
7. 자동화 위험이면 보류 조건과 필요한 추가 문맥을 적는다.
8. 원본 코드 의미가 드러나는 업무 로직 설명은 제거하고 제어 흐름/타입/자원/예외 처리 구조만 남긴다.
9. 결과는 references/real-fix-patterns/<CHECKER_KEY>.md 형식으로 작성한다.
```

## CLI/LLM 반영 기준

패턴 문서가 생겼다고 바로 자동화하지 않는다.

Track A/B CLI에 반영 가능한 경우:

- before/after가 구문적으로 명확하다.
- Roslyn으로 타입/문법 guard를 둘 수 있다.
- 정적 타입 축소, 소유권 변경, 예외 의미 변경이 없다.
- 실패 시 빌드 실패로 감지 가능하거나 안전하게 skip 가능하다.

Track C LLM 가이드에 반영할 경우:

- 체커 결함 판별 기준을 더 명확히 해준다.
- `보류`와 `수정`을 가르는 판단 포인트가 있다.
- 수정 후에도 .NET Framework 4.7.2 / C# 7.3 문법을 지킨다.
- 업무 의미 없이도 일반화 가능한 예시다.

반영 금지:

- 업무 도메인 의미가 남아 있는 예시.
- 전체 함수/파일에 가까운 긴 코드.
- 빌드/동작 보존 조건을 설명할 수 없는 수정.
- 특정 프로젝트 내부 API 의미를 알아야만 판단 가능한 수정.
