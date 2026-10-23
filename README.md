# CampusLink 工作区

广州大学校园网（锐捷 SAM+ / eportal）认证工具的开发工作区。

```
CampusLink/
├─ CampusLink/        应用源码（WPF, .NET Framework 4.8）+ 使用文档
│  └─ README.md       ← 功能、构建、分发、协议、预设、排错
├─ tests/CoreTests/   协议层与更新/预设/脱敏自动化测试（自带 mock 服务）
├─ tools/             图标生成、一键打包（package.ps1）
├─ installer/         Inno Setup 安装脚本
├─ .github/workflows/ GitHub Actions：打 tag 自动构建并发布 Release
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

## 打包（安装程序 + 更新 zip）

```powershell
powershell -ExecutionPolicy Bypass -File tools\package.ps1
# 产物：dist\CampusLink-Setup-<版本>.exe 和 dist\CampusLink-<版本>-win.zip
```

## 发布

推一个 `v*` tag 即可触发 GitHub Actions（构建 → 打包 zip + Setup → 创建 Release）：

```bash
git tag v1.3.0
git push origin v1.3.0
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
