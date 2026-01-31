# PGD继承UE Tasks并行化能力方案设计

## 将PGD引入到UnrealCSharp中
`UnrealCSharpPlugins/UnrealCSharp/Source/UnrealCSharpCore/Private/Common/FUnrealCSharpFunctionLibrary.cpp`的`GetFullAssemblyPublishPath()`方法中通过`GetFullCustomProjectsPublishPath()`读取配置里的 CustomProjects 列表生成路径：
```cpp
TArray<FString> FUnrealCSharpFunctionLibrary::GetFullAssemblyPublishPath()
{
    return TArrayBuilder<FString>().
           Add(GetFullUEPublishPath()). // UE.dll
           Add(GetFullGamePublishPath()).// Game.dll
           Append(GetFullCustomProjectsPublishPath()). // 自定义程序集
           Build();
}
  
TArray<FString> FUnrealCSharpFunctionLibrary::GetFullCustomProjectsPublishPath()
{
    TArray<FString> FullCustomProjectsPublishPath;

    if (const auto UnrealCSharpSetting = GetMutableDefaultSafe<UUnrealCSharpSetting>())
    {
        // 读取 DefaultUnrealCSharpSetting.ini中配置的CustomProjects
        for (const auto& [Name] : UnrealCSharpSetting->GetCustomProjects())
        {
            FullCustomProjectsPublishPath.Add(GetFullPublishDirectory() / Name + DLL_SUFFIX);
        }
    }

    return FullCustomProjectsPublishPath;
}
```
先将PGD.dll以及其他自定已程序集放入项目根目录的Libraries/目录下统一维护，并在Scripts的Game.csproj（视项目实际命名）中添加reference：
```c#
<ItemGroup>
  <Reference Include="PGD">
      <HintPath>..\..\Libraries\PGD.dll</HintPath>
  </Reference>
</ItemGroup>
```
然后在根目录`Config/DefaultUnrealCSharpSetting.ini`中添加`+CustomProjects=(Name="PGD")`:
```ini
[/Script/UnrealCSharpCore.UnrealCSharpSetting]
bEnableDebug=False
Host=127.0.0.1
Port=50000
+CustomProjects=(Name="PGD")
```
测试PGD是否被引入：
```cs
var world = new IECSWorld();
var entity = world.CreateEntity(10);
Console.WriteLine($"=====ecsworld entity num: {world.EntityNum}, id: {entity.Id}");

// 打出日志
=====ecsworld entity num: 1, id: 10
```

## PGD集成UE Tasks并行化能力
PGD集成UE Tasks并行化能力本质是在UE Tasks中执行开发者预定义好的C#托管任务，将Chunk遍历处理的逻辑直接仿佛Tasks中，由UE底层进行任务线程调度。

### 任务Handle使用注意事项

**句柄生命周期规则**
- ScheduleUeParallel 返回的handle在handle.Wait()后必须释放
- 同一个 handle 只允许被 Combine 一次。Combine 后，原 handle 进入“已转移”状态，不可再次使用
- Combine 之前，不要 Dispose 原 handle。Dispose 会使 native map 中移除任务，Combine 会失败
- 不要混用 Wait/IsCompleted + Combine。如果已经 Wait 完成，可直接 Dispose，不需要再 Combine

**Combine正确用法**
- 必须传入仍然有效的 handle
- Combine 后只使用返回的新 handle，原 handle 进入“已转移/不可用”状态

### 性能测试

#### 测试环境与参数

| 参数 | 值 | 说明 |
|-----|------|------|
| ENTITY_COUNT | 40,000 | 单种组件组合的实体数量 |
| ITERATION | 16 | 性能测试迭代次数 |
| EXECUTE_ROUND | 20 | 执行轮数（用于取平均值） |
| WORK | 128 | 单元素计算负载（模拟计算循环次数） |

#### 实体构建策略

测试使用 7 种组件组合类型，每种组合创建 `ENTITY_COUNT`（40,000）个实体，总计约 280,000 个实体：

```cs
// 1. PGDPosition + PGDRotation
// 2. PGDPosition + PGDRotation + Health
// 3. PGDPosition + PGDRotation + Mana
// 4. PGDPosition + PGDRotation + Mana + Health
// 5. PGDPosition + PGDRotation + Mana + PGDScale
// 6. PGDPosition + PGDRotation + Mana + PGDTransform
// 7. PGDPosition + PGDRotation + Mana + Health + PGDScale
```

这种多样化组合旨在模拟真实场景中 Entity 组件构成的差异性，测试 Query 在多 Archetype 下的遍历与调度性能。

#### 测试场景

##### ScheduleUeParallel + 多Query并行 + 逐个 Dispose

```cs
var handles = new List<UETasksJobHandle>();
var h1 = queryPos.ScheduleUeParallel((ref PGDPosition pos, IEntity entity) =>
{
    float v = pos.x;
    for (int k = 0; k < WORK; k++)
    {
        v = v * 1.001f + 0.1f;
    }
    pos.x = v;
});
var h2 = queryRot.ScheduleUeParallel((ref PGDRotation rot, IEntity entity) => { ... });
var h3 = queryhealth.ScheduleUeParallel((ref Health health, IEntity entity) => { ... });
var h4 = querymana.ScheduleUeParallel((ref Mana mana, IEntity entity) => { ... });

// 逐个 Dispose
for (int j = 0; j < handles.Count; j++)
{
    handles[j].Dispose();
}
```

##### ScheduleParallel对比测试

```cs
var h1 = queryPos.ScheduleParallel((ref PGDPosition pos, IEntity entity) => { ... });
ScheduleParallelHandleStore.Add(h1);
// ... h2, h3, h4
ScheduleParallelHandleStore.CompleteAll();
```

#### 测试结论

在中高负载的场景下，UE Tasks的优势非常明显。而在轻负载时，PGD内部的并行能力因调度路径更轻，整体效率会更高。