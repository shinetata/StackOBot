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