# CampusLink 工作区

广州大学校园网（锐捷 SAM+ / eportal）认证工具的开发工作区。

```
CampusLink/
├─ CampusLink/        应用源码（WPF, .NET Framework 4.8）+ 使用文档
│  └─ README.md       ← 功能、构建、协议说明、常见问题
├─ tests/CoreTests/   协议层自动化测试（自带 mock 门户，不依赖校园网）
├─ tools/             开发辅助脚本（SVG → 多尺寸 ICO）
├─ docs/              截图与交付说明
│  ├─ screenshots/    界面截图
│  └─ 交付说明.md      验证记录、已知限制、后续计划
└─ README.md
```

## 构建

```powershell
cd CampusLink
msbuild CampusLink.csproj -t:Restore,Build -p:Configuration=Release
# 产物 bin\Release\CampusLink.exe
```

## 运行测试

```powershell
# 先构建应用（测试引用 CampusLink.exe）
cd CampusLink
msbuild CampusLink.csproj -t:Restore,Build -p:Configuration=Debug

cd ..\tests\CoreTests
msbuild CoreTests.csproj -t:Restore,Build
.\bin\Debug\CoreTests.exe
```

## 重新生成图标

```bash
cd tools
npm i sharp
node build-icon.js ..\CampusLink\Resources\app-icon.svg ..\CampusLink\Resources
```

使用说明见 [CampusLink/README.md](CampusLink/README.md)，交付与验证记录见 [docs/交付说明.md](docs/交付说明.md)。
