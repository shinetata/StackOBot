# UnrealCSharp 最小可用闭环：`NativeBuffer<T>`（native-backed）+ internal call（C#→UE C++ 同进程遍历处理）

本文目标：在 **不引入 TaskGraph 调度** 的前提下，先把“C# 拥有一段 native 连续内存（native-backed），并通过 UnrealCSharp 的 internal call 让 UE C++ 以指针视图遍历处理，然后把结果写回给 C#”这条最小闭环讲清楚，并给出可直接落地的最小代码草案。

> 非目标：本文不讨论并行化（TaskGraph）、跨线程生命周期、结构变更/扩容与 job barrier 的完整治理体系；那些会在评估通过后再进入代码阶段。

---

## 1) `native-backed` 是什么（在我们这里的口径）

`native-backed` 指：数据**不放在 C# 托管堆（`T[]`）**里，而是放在 **非托管/原生内存**（unmanaged/native heap）里。

于是我们可以同时提供两种“视图”：

- C#：`Span<T>` 视图（用于写入数据、读取结果）
- UE C++：`T* + length` 视图（用于遍历处理、原地写回）

两边看的都是**同一块内存**，不存在“复制/回写”的额外成本。

---

## 2) 最小闭环的组成（必须包含 + 可选）

本文的最小闭环包含两部分（都必须有）：

1) C# `NativeBuffer<T>`：负责分配/释放 native 内存，暴露 `Span<T>`，提供 `nint Ptr` 给 native 使用，并内置调试/防呆
2) UnrealCSharp internal call：
   - C#：`[MethodImpl(MethodImplOptions.InternalCall)] extern ...`
   - C++：`FClassBuilder(...).Function("Xxx", XxxImplementation)`

**不包含**：TaskGraph 调度（后续再接）。

---

## 3) C#：`NativeBuffer<T>`（带调试/防呆）

设计目标：

- `T` 必须是 `unmanaged`（否则 C++ 无法按字节/结构体安全解释）
- `Span<T>` 只作为“临时视图”使用；禁止缓存到字段（避免用到已 Realloc/Free 的旧指针）
- 提供 `Version`，每次 `Realloc` 增加，用于 Debug/断言“视图是否过期”
- 防呆：disposed 检查、容量/长度边界检查

> 说明：UnrealCSharp 的脚本工程允许 `unsafe`（仓库里已有 `unsafe` 用法，例如 `Plugins/UnrealCSharp/Script/UE/Library/FTaskGraphImplementation.cs`）。

```csharp
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Script.Library;

public unsafe sealed class NativeBuffer<T> : IDisposable where T : unmanaged
{
    private void* _data;
    private bool _disposed;

    public int Length { get; private set; }
    public int Capacity { get; private set; }

    // 每次发生 realloc/resize（导致指针可能变化）就递增，用于调试与防呆
    public uint Version { get; private set; }

    public nint Ptr
    {
        get
        {
            ThrowIfDisposed();
            return (nint)_data;
        }
    }

    public NativeBuffer(int length, bool clear = true)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        Allocate(capacity: length, length: length, clear: clear);
    }

    public Span<T> AsSpan()
    {
        ThrowIfDisposed();
        return new Span<T>(_data, Length);
    }

    public Span<T> AsSpan(int start, int length)
    {
        var span = AsSpan();
        return span.Slice(start, length);
    }

    public void Resize(int newLength, bool clearNew = true)
    {
        ThrowIfDisposed();

        if (newLength < 0) throw new ArgumentOutOfRangeException(nameof(newLength));
        if (newLength > Capacity) EnsureCapacity(newLength, clearNew: false);

        if (clearNew && newLength > Length)
        {
            AsSpan(Length, newLength - Length).Clear();
        }

        Length = newLength;
    }

    public void EnsureCapacity(int minCapacity, bool clearNew = false)
    {
        ThrowIfDisposed();

        if (minCapacity < 0) throw new ArgumentOutOfRangeException(nameof(minCapacity));
        if (minCapacity <= Capacity) return;

        int newCapacity = Math.Max(minCapacity, Capacity == 0 ? 4 : Capacity * 2);
        nuint newBytes = (nuint)(Unsafe.SizeOf<T>() * newCapacity);

        if (_data == null)
        {
            _data = NativeMemory.Alloc(newBytes);
            Version++;
            if (clearNew) new Span<byte>(_data, (int)newBytes).Clear();
        }
        else
        {
            nuint oldBytes = (nuint)(Unsafe.SizeOf<T>() * Capacity);
            _data = NativeMemory.Realloc(_data, newBytes);
            Version++;
            if (clearNew && newBytes > oldBytes)
            {
                new Span<byte>((byte*)_data + (int)oldBytes, (int)(newBytes - oldBytes)).Clear();
            }
        }

        Capacity = newCapacity;
        Debug.Assert(_data != null);
    }

    public override string ToString()
        => _disposed
            ? $"{nameof(NativeBuffer<T>)}(disposed)"
            : $"{nameof(NativeBuffer<T>)}(T={typeof(T).Name}, Len={Length}, Cap={Capacity}, Ptr=0x{Ptr.ToString("X")}, Ver={Version})";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_data != null)
        {
            NativeMemory.Free(_data);
            _data = null;
        }

        Length = 0;
        Capacity = 0;
        Version++;
    }

    private void Allocate(int capacity, int length, bool clear)
    {
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (length < 0 || length > capacity) throw new ArgumentOutOfRangeException(nameof(length));

        nuint bytes = (nuint)(Unsafe.SizeOf<T>() * capacity);
        _data = bytes == 0 ? null : NativeMemory.Alloc(bytes);
        Version++;

        Capacity = capacity;
        Length = length;

        if (clear && _data != null)
        {
            new Span<byte>(_data, (int)bytes).Clear();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NativeBuffer<T>));
    }
}
```

使用注意（防踩坑）：

- **不要缓存 `Span<T>`**：只能在方法内部临时拿 `AsSpan()` 用完就丢。
- **不要在 `Dispose()` 后再调用 internal call**：指针已释放。
- `Version` 的意义是“帮助你 Debug 旧视图是否可能过期”，它不能魔法般阻止你滥用 `Span<T>`。

### 3.1 什么叫“不要缓存 `Span<T>`”

这里的“缓存”指：把某次调用 `AsSpan()` 得到的 `Span<T>` **保存到字段/静态变量/闭包里**，让它活过当前方法作用域，之后再继续使用。

为什么这很危险：

- `Span<T>` 只是“指针 + 长度”的轻量视图，本身**不拥有内存**。
- `NativeBuffer<T>.EnsureCapacity/Resize/Dispose` 可能导致底层指针变化或内存被释放；你手里那个旧 `Span<T>` 会变成“悬空视图”。
- 一旦悬空，轻则数据错、重则 native 崩溃（C# 侧也可能直接访问冲突）。

### 3.2 典型“缓存/滥用 `Span<T>`”方式（反例）

#### 反例 A：把 `Span<T>` 存成字段（最常见）

```csharp
public unsafe sealed class BadCache<T> where T : unmanaged
{
    private readonly NativeBuffer<T> _buf = new(16);
    private Span<T> _cached; // ❌ 缓存

    public BadCache()
    {
        _cached = _buf.AsSpan();
    }

    public void Grow()
    {
        _buf.EnsureCapacity(1024); // 可能 realloc，指针变化
        _cached[0] = default;      // ❌ 仍在用旧 span（悬空风险）
    }
}
```

#### 反例 B：跨 `await` 使用 `Span<T>`（更隐蔽）

```csharp
public static async System.Threading.Tasks.Task BadAsync()
{
    using var buf = new NativeBuffer<int>(8);
    var span = buf.AsSpan();

    await System.Threading.Tasks.Task.Delay(1);

    // ❌ 这段时间里如果 buf 被别处释放/扩容（或你把 span 捕获到别处），就有风险
    span[0] = 123;
}
```

> 注：就算这里 `buf` 没被释放，跨 `await` 也会让“谁在何时改了底层指针/生命周期”变得难以审计；工程上建议把 `Span<T>` 当作“同步临时视图”。

#### 反例 C：把 `Span<T>` 传进闭包/事件，延迟执行

```csharp
public static void BadClosure()
{
    using var buf = new NativeBuffer<int>(8);
    var span = buf.AsSpan();

    System.Action later = () => span[0] = 1; // ❌ 捕获 span

    buf.Dispose();
    later(); // ❌ 已释放内存上的 span
}
```

#### 反例 D：缓存元素引用（`ref`）并在扩容后继续用

```csharp
public static void BadRef()
{
    using var buf = new NativeBuffer<int>(4);
    var span = buf.AsSpan();

    ref int r = ref span[0]; // ❌ 绑定到当前指针
    buf.EnsureCapacity(1024); // realloc 可能发生

    r = 7; // ❌ ref 指向旧内存
}
```

### 3.3 推荐用法（正例）

把 `Span<T>` 当作“借来的视图”，每次使用前现取现用：

```csharp
using var buf = new NativeBuffer<int>(8);
buf.AsSpan().Clear();
buf.AsSpan()[0] = 1;

buf.EnsureCapacity(1024);
buf.AsSpan()[0] = 2; // ✅ realloc 后重新获取 span
```

---

## 4) C#：internal call 声明 + 演示调用（同步）

UnrealCSharp 的 internal call 命名模式（从仓库现有代码归纳）：

```
Script.Library.<ClassName>Implementation
  - extern 方法名：<ClassName>_<FunctionName>Implementation
UE C++ 注册：
  - FClassBuilder(TEXT("<ClassName>"), NAMESPACE_LIBRARY).Function(TEXT("<FunctionName>"), ...)
```

示例：我们假设注册一个 `FNativeBufferKernel.AddOneAndSumInt32`，那么 C# 侧声明应为：

```csharp
using System.Runtime.CompilerServices;

namespace Script.Library;

public static class FNativeBufferKernelImplementation
{
    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern int FNativeBufferKernel_AddOneAndSumInt32Implementation(nint data, int length);
}
```

演示调用（同步调用，C++ 返回前不会并发访问该指针）：

```csharp
using System;
using Script.Library;

public static class NativeBufferInternalCallDemo
{
    public static void Run()
    {
        using var buf = new NativeBuffer<int>(length: 8);
        var span = buf.AsSpan();
        for (int i = 0; i < span.Length; i++) span[i] = i;

        int sum = FNativeBufferKernelImplementation.FNativeBufferKernel_AddOneAndSumInt32Implementation(buf.Ptr, buf.Length);

        Console.WriteLine($"{buf} sum={sum} first={span[0]} last={span[^1]}");
    }
}
```

---

## 5) UE C++：internal call 注册 + 遍历处理（原地写回）

示例 C++（仅表达形态；落地时应放到 `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/` 并按现有模式编译）：

```cpp
#include "Binding/Class/FClassBuilder.h"
#include "CoreMacro/NamespaceMacro.h"

namespace
{
	struct FNativeBufferKernel
	{
		static int32 AddOneAndSumInt32Implementation(const void* InData, const int32 InLength)
		{
			if (InData == nullptr || InLength <= 0)
			{
				return 0;
			}

			int32* Data = static_cast<int32*>(const_cast<void*>(InData));

			int64 Sum = 0;
			for (int32 i = 0; i < InLength; ++i)
			{
				Data[i] += 1;
				Sum += Data[i];
			}

			return static_cast<int32>(Sum);
		}

		FNativeBufferKernel()
		{
			FClassBuilder(TEXT("FNativeBufferKernel"), NAMESPACE_LIBRARY)
				.Function(TEXT("AddOneAndSumInt32"), AddOneAndSumInt32Implementation);
		}
	};

	[[maybe_unused]] FNativeBufferKernel NativeBufferKernel;
}
```

---

## 6) 这个最小闭环能证明什么、不能证明什么

能证明：

- C# 能在 UnrealCSharp 环境中持有 native 连续内存（`NativeBuffer<T>`）
- internal call 能把 `nint` 指针传给 UE C++，并以 `T*` 视图遍历处理
- C++ 原地写回，C# 通过 `Span<T>` 读到结果（零拷贝）

不能证明（需要下一阶段）：

- 并行化（TaskGraph worker）下的生命周期/同步/禁止区治理
- 结构变更/扩容与 in-flight job 的冲突收敛
- 对复杂组件（含引用字段）的支持（本文明确不支持）
