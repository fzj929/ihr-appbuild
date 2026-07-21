# ihr-appbuild

## 铸版台 · 软件发布管理平台

基于 .NET 8 Web API、EF Core、SQLite 和 Vue 3 的软件发布管理平台，提供 SVN 获取、后台发布、双前端构建、配置版本、运行控制、回滚、发布包与审计功能。

## 前置条件

- .NET 8 SDK
- Node.js 与 npm
- SVN 命令行客户端
- Windows 部署脚本需要管理员 PowerShell
- Linux 部署脚本需要 root、systemd 和 ASP.NET Core 8 Runtime

## 脚本说明

所有脚本位于 `scripts` 目录：

| 脚本 | 用途 |
|---|---|
| `build.ps1` / `build.sh` | 安装依赖并构建前后端，结果输出到 `artifacts/packages/<时间戳>` |
| `deploy-windows.ps1` | 部署并注册 Windows 服务 |
| `start-windows.ps1` / `stop-windows.ps1` | 启停 Windows 服务 |
| `uninstall-windows.ps1` | 卸载 Windows 服务，默认保留数据 |
| `deploy-linux.sh` | 部署并注册 systemd 服务 |
| `start-linux.sh` / `stop-linux.sh` | 启停 systemd 服务 |
| `uninstall-linux.sh` | 卸载 Linux 服务，默认保留数据 |
| `nginx-manager.service` | Linux systemd 服务模板（文件名遵循部署脚本约定） |

每次构建使用独立时间戳目录，并更新 `artifacts/latest-build.txt`。部署脚本自动选择最新构建，因此平台运行期间也能安全构建。

## Windows

构建不需要管理员权限：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

以管理员 PowerShell 部署：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\deploy-windows.ps1
```

默认安装到 `C:\ProgramData\ReleaseManager`，注册服务名 `ReleaseManager`，监听端口 `5088`。可覆盖参数：

```powershell
.\scripts\deploy-windows.ps1 -InstallDirectory D:\ReleaseManager -Port 8080
```

停止、启动和卸载：

```powershell
.\scripts\stop-windows.ps1
.\scripts\start-windows.ps1
.\scripts\uninstall-windows.ps1
```

卸载默认保留 `data`。确认连数据一起清除时使用：

```powershell
.\scripts\uninstall-windows.ps1 -RemoveData
```

## Linux

```bash
chmod +x scripts/*.sh
./scripts/build.sh
sudo SKIP_BUILD=1 ./scripts/deploy-linux.sh
```

默认安装目录为 `/opt/release-manager`，数据目录为 `/var/lib/release-manager`，systemd 服务名为 `release-manager`，监听端口 `5088`。

```bash
sudo ./scripts/stop-linux.sh
sudo ./scripts/start-linux.sh
sudo ./scripts/uninstall-linux.sh
```

卸载默认保留数据；彻底清除数据需显式执行：

```bash
sudo ./scripts/uninstall-linux.sh --purge-data
```

## 初始账户

```text
用户名：admin
密码：Admin@123456
```

首次登录后请立即修改密码。生产环境还应配置 HTTPS，并限制平台数据目录的操作系统访问权限。
