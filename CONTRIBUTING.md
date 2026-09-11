# Contributing to LiQuota

感谢你愿意改进 LiQuota。

## 开发环境

- Windows 10/11
- .NET 8 SDK
- Codex Desktop（仅实时联调需要）

## 提交前检查

```powershell
dotnet restore .\LiQuota.csproj
dotnet build .\LiQuota.csproj -c Release --no-restore
dotnet run --project .\tests\LiQuota.SmokeTests\LiQuota.SmokeTests.csproj -c Release
```

请同时确认：

- 100%、125%、150% 和 200% DPI 下徽标仍位于标题栏安全区域
- 浅色、深色模式可读
- Codex 最小化、关闭、重开和账号切换时行为正常
- 日志、截图、Issue 和测试数据不包含完整邮箱、账号 ID、令牌或其他隐私信息

## Pull Request

一个 PR 尽量只解决一个问题。请说明修改动机、测试环境、验证结果；视觉改动请附脱敏截图。
