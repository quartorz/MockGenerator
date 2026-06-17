# ジェネリック interface の Mock の使い方

`[GenerateMockView]` が生成する Mock では、View が実装する **ジェネリックな `[Input]`/`[Output]` interface** を「アクセサ方式」で扱います。この文書はその使い方をまとめたものです。

非ジェネリックな interface は従来どおり Mock 直下にフラットなメンバー（必要なら interface 名 prefix 付き）として出るので、本書はジェネリック interface のみを対象にします。

---

## なぜアクセサ方式なのか

`IFoo<int>` と `IFoo<string>` のように **同じジェネリック interface を異なる型引数で複数実装**すると、メンバー名（例 `value`）が同じになり、フラットに展開すると衝突します。型引数を名前に埋め込む（`IFoo_Int_value` のような）案は、`IFoo<List<int>>` のような入れ子で破綻します。

そこで、型引数を**メソッドの型引数に載せて C# の型システムにそのまま解決させる**のがアクセサ方式です。

```csharp
mock.AsIFoo<int>().value = 3;
mock.AsIFoo<string>().value = "hello";
mock.AsIFoo<List<int>>().value = new List<int>();   // 入れ子も型引数の個数も無制限
```

---

## 生成されるもの

次の View を例にします。

```csharp
using MockGenerator;

[Output]
public interface IFoo<T>
{
    T value { set; }          // setter（Output）
    T current { get; }        // getter（Input 的な読み出し）
    void Push(T item);        // メソッド
    event System.Action<T> OnChanged;  // イベント
}

[GenerateMockView]
public partial class C : UnityEngine.MonoBehaviour, IFoo<int>, IFoo<string>
{
    // IFoo<int> は public で暗黙実装、IFoo<string> は明示実装、など
}
```

ジェネリック interface 定義 `IFoo<T>` ごとに、Mock の中へ次が生成されます。

### 1. アクセサ型 `IFooAccessor<T>`

interface のメンバーを **テスト操作しやすい形**で保持します。

| 元のメンバー | アクセサ側 |
|---|---|
| `T value { set; }` | `value` setter ＋ `OnValueSet`（`System.Action<T>`）＋内部 backing。`set` するたび `OnValueSet` が発火 |
| `T current { get; }` | `current { get; set; }`（テストから戻り値をセットできるよう get/set 両方） |
| `void Push(T item)` | スタブ用デリゲートホルダ `PushFunc` ＋転送メソッド `Push` |
| `event Action<T> OnChanged` | `OnChanged` イベント ＋ 発火用 `RaiseOnChanged(...)` |

### 2. 入口メソッド `AsIFoo<T>()`

型引数に対応するアクセサ実体を返します（`typeof` でキャッシュ）。同じ型引数なら毎回同じインスタンスが返るので、設定と検証で実体が一致します。

### 3. ルーティング

各 interface のメンバーは、`AsIFoo<...>()` 経由でアクセサへ転送されます。

- View が **暗黙実装**しているメンバー → Mock にも **public メンバー**を出し、アクセサへ転送（`mock.value` でも `mock.AsIFoo<int>().value` でも同じ場所を叩く）。
- View が **明示実装**しているメンバー → public は出さず、明示 interface 実装だけ出してアクセサへ転送（`((IFoo<string>)mock).value` 経由でアクセサに届く）。

---

## テストでの使い方

### 値の設定と履歴の取得（setter / Output）

```csharp
var mock = new MockView.MockC();

// 値をセット
mock.AsIFoo<int>().value = 42;

// セットされた値の履歴を購読
var log = new List<int>();
mock.AsIFoo<int>().OnValueSet += v => log.Add(v);

mock.AsIFoo<int>().value = 1;
mock.AsIFoo<int>().value = 2;
// log == [1, 2]
```

`IFoo<int>` と `IFoo<string>` は完全に別のストレージなので干渉しません。

```csharp
mock.AsIFoo<int>().value = 10;
mock.AsIFoo<string>().value = "x";
// それぞれ独立
```

### 読み出し値の差し込み（getter / Input）

```csharp
mock.AsIFoo<int>().current = 99;

// SUT が IFoo<int>.current を読むと 99 が返る
IFoo<int> asInterface = mock;
Assert.AreEqual(99, asInterface.current);
```

### メソッドのスタブ

メソッドは `{Name}Func` にデリゲートを差し込んで戻り値や副作用を定義します（未設定なら戻り値は `default`）。

```csharp
// 単一メソッドは {Name}Delegate 型のデリゲートを直接代入
mock.AsIFoo<int>().PushFunc = item => System.Console.WriteLine(item);

IFoo<int> asInterface = mock;
asInterface.Push(5);   // PushFunc が呼ばれる
```

> オーバーロードやジェネリックメソッドがある場合は、`{Name}Func` が `I{Name}Delegate` というネスト interface 型になり、各オーバーロードを `Call` で実装する形になります（既存の非ジェネリック Mock と同じ規約）。

### イベントの発火

```csharp
var received = new List<int>();
mock.AsIFoo<int>().OnChanged += v => received.Add(v);

// テストから発火
mock.AsIFoo<int>().RaiseOnChanged(7);
// received == [7]
```

### 入れ子・多型引数

型引数は C# の型システムがそのまま解決するので、入れ子の深さや個数に制限はありません。

```csharp
mock.AsIFoo<List<int>>().value = new List<int> { 1, 2 };
mock.AsIFoo<Dictionary<string, IBar<int>>>().value = ...;

// IDict<K,V> のような多型引数 interface なら
mock.AsIDict<string, int>().value = ...;
```

---

## 暗黙 / 明示の混在について

同じ名前の public メンバーは 1 つの型しか持てないため、`IFoo<int>` と `IFoo<string>` を両方実装する場合は **どちらか一方だけが暗黙（public）実装で、残りは明示実装**になります（これがコンパイル可能な唯一の形）。

どちらであっても、テストからは **`AsIFoo<...>()` 経由で同じように**操作できます。暗黙側は加えて `mock.value` のような public アクセスも可能、というだけの違いです。

メソッドはオーバーロードできるため、`IFoo<int>` と `IFoo<string>` の両方を暗黙（public）実装することもできます。その場合も `AsIFoo<int>()` / `AsIFoo<string>()` でそれぞれにアクセスできます。

---

## 既知の制限

1. **同名・同型引数数の別ジェネリック interface**（別 namespace に同名の `IFoo<T>` が 2 つある等）は、`AsIFoo` / `IFooAccessor` という生成名が衝突します。現状は片方の名前空間を分けるなどの回避が必要です。

2. **1 つの暗黙実装メンバーが、異なるジェネリック interface を同一の閉じ型で同時に満たす**ケース（例: `public int value` が `IFoo<int>.value` と `IBar<int>.value` の両方を暗黙実装）では、public ミラーは 1 つに統合され、先頭の `AsXxx` へ転送されます。両 interface の契約自体は満たされますが、もう一方の `AsXxx<int>()` は別ストレージになり、そちらの `On...Set` 購読は発火しません。このケースのテストは、転送先になっている側の `AsXxx` を購読してください。

---

## `[GenerateMockFor]` でも同じ

`[GenerateMockFor(typeof(IFoo<int>))]` / `[GenerateMockFor(typeof(IFoo<string>))]` のようにジェネリック interface を指定した場合も、同じアクセサ方式（`AsIFoo<T>()`）が対象のクラス自身に生成されます。

GenerateMockView との違いは2点だけ：

- View が無く、ジェネレータが実装そのものを作るので、ジェネリック interface のメンバーは **常に explicit interface 実装**でアクセサへ転送されます（public ミラーは出ません）。アクセス入口は `AsIFoo<...>()` に一本化されます。
- アクセサのプロパティは GenerateMockFor 既存の規約に合わせ、get/set の有無に関わらず **常に `On{Name}Set` Action + get/set ストレージ**になります。

```csharp
[GenerateMockFor(typeof(IFoo<int>))]
[GenerateMockFor(typeof(IFoo<string>))]
public partial class MyMock { }

var m = new MyMock();
m.AsIFoo<int>().value = 1;
m.AsIFoo<string>().value = "x";
((IFoo<int>)m).Push(1);   // explicit 実装経由でアクセサに届く
```

## 関連

- 非ジェネリック interface・複数ソースの prefix 規約は別途（従来どおり）。
- setter の `On{Name}Set` Action / backing 規約は通常の Mock 生成と共通です。
