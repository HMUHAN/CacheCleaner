# 缓存清理（CacheCleaner）

一个 Windows 小工具：按计划自动清理 **QQ（NT 版）** 和 **微信** 的本地聊天缓存（图片、视频、表情、语音），
不修改任何客户端，随客户端更新也不受影响。

- 双标签设置界面：**微信** / **QQ**，两边配置完全独立
- 只做一件事：**容量上限**——缓存超过 N MB 时删除旧文件（降到上限的 90% 以下）或全部删除
- 常驻托盘，按「离上限还有多远」每 5~30 分钟检查一次（离得远就查得稀）；勾上「开启」则只在开机检查一次后退出
- 手动清理随时可用，与自动检查互不影响
- 清理完成用右下角不抢焦点的提示条
- 支持检查更新（读取 `latest.json`，提示打开下载页）

---

## 下载与安装

到 [Releases](../../releases) 下载 **`CacheCleaner-<版本>.zip`**，解压到任意文件夹（不要直接在桌面或下载目录里运行），
然后双击 `QQCacheTray.exe`。

> 为什么要用压缩包：程序会在 **exe 同级目录**生成 `data\`（配置、日志）。
> 直接双击桌面上的 exe 会把 `data\` 建在桌面上；解压到文件夹就没这个问题。
>
> 之后的**升级**只需要下载 `QQCacheTray.exe` 覆盖旧文件，或者在程序里点「立即更新」。

程序自带 `requireAdministrator` 清单，**启动时会出现 UAC 提示，点“是”**。
（本工具需要删除 `C:\Users\...` 下的缓存，没有管理员权限做不到。）

首次运行会在程序目录生成：

```
data\config.ini          配置
data\last-clean.json     QQ 上次清理时间
data\wx-last-clean.json  微信上次清理时间
data\logs\               日志（保留 20 份）
```

**升级时直接覆盖 exe 即可，`data\` 不会被覆盖，配置和计时都保留。**

---

## 使用

1. 双击 `QQCacheTray.exe` → UAC 点“是”，托盘出现图标；
2. 右键托盘 → 设置；
3. 需要开机自动运行，勾选「开机自动启动（登录时请求管理员权限）」。

> 程序会在「启动」文件夹放一个快捷方式「QQ 缓存管家」（登录时请求一次 UAC 后启动，任务管理器「启动」里显示的就是这个名字）。
> 如果这个启动项被 Windows「任务管理器 → 启动」关掉，程序下次启动会自动重新启用（日志里会有“已修复开机自启”）。
> 非交互启动的进程拿不到权限，所以只能用这种方式。

### 清理规则

```
缓存超过 N MB 时：
  · 删除旧文件 → 按修改时间从旧到新删，降到上限的 90% 以下
  · 全部删除   → 把该类缓存清空
```

| 设置 | 行为 |
| --- | --- |
| 勾选「启用容量上限清理」+ 勾选「开启」 | **开机只按上限检查一次**，弹出结果气泡后退出，不常驻 |
| 勾选「启用容量上限清理」+ 不勾「开启」（默认） | 常驻托盘，每 5~30 分钟检查一次（离上限越远查得越稀） |
| 两个应用都不勾「启用」 | 开机只亮一下图标 + 气泡说明，不做任何事，随后退出 |

> 保存设置时，只要不是「两边都常驻」，会弹一句开机行为说明（哪个常驻、哪个只检查一次），点「知道了」继续保存。

> 旧版本的「定时清理（每隔 N 天 / 每次 HH:mm / 保留最近 N 天）」已移除。
> 升级后原来用定时的应用会变成**未启用**，需要的话手动勾上「启用容量上限清理」并设置 MB 上限。

---

## 命令行

```bat
QQCacheTray.exe              :: 托盘界面（会弹 UAC）
QQCacheTray.exe --boot       :: 开机自启：没启用容量上限时提示一下然后退出
QQCacheTray.exe --limit-now  :: 立刻按容量上限检查一次，跑完就退
QQCacheTray.exe --selftest   :: 界面/配置自检
QQCacheTray.exe --diag       :: 打印运行环境
```

---

## 自行编译

需要 Windows 自带的 .NET Framework 4.x 编译器，无需安装 SDK：

```powershell
powershell -ExecutionPolicy Bypass -File tools\build.ps1 -Final
```

产物是 `QQCacheTray.exe`（单文件，图标和清单已内嵌）。

---

## 检查更新

程序启动时（每天一次）或点托盘菜单「检查更新…」会拉取：

```
https://raw.githubusercontent.com/<你的用户名>/<仓库名>/main/latest.json
```

内容格式：

```json
{
  "version": "1.1.0",
  "url": "https://github.com/<用户名>/<仓库名>/releases/download/v1.1.0/QQCacheTray.exe",
  "sha256": "…",
  "notes": "修复了…"
}
```

比当前版本新就弹提示：

- 清单里**有 `sha256`** → 显示「立即更新」：自动下载、校验哈希、关闭程序、替换 exe 并重启；
- 清单里**没有 `sha256`** → 只显示「打开下载页」，手动下载覆盖（安全兜底）。

哈希不匹配会直接丢弃下载文件并报错，不会执行替换。

**要改这个地址，只需改一处**：`src/QQCacheTray.cs` 里的
`UpdateChecker.DefaultManifestUrl`；也可以在 `data\config.ini` 里加 `update_url=...` 覆盖。

---

## 发布新版本

1. 改版本号：`src/QQCacheTray.cs` 的 `AssemblyVersion`、`app.manifest` 的 `version`；
2. 提交并打标签：

```bash
git tag v1.1.0
git push origin v1.1.0
```

GitHub Actions 会自动编译、生成 Release、上传 exe，并把新的 `latest.json` 提交回 `main`。

---

## 目录结构

```
.
├── src/QQCacheTray.cs          # 单文件源码
├── app.manifest                # 提权清单
├── assets/QQCacheTray.ico      # 图标
├── tools/build.ps1             # 编译脚本
├── tools/make_icon3.py         # 由 PNG 生成多尺寸 ICO（需 Pillow）
├── latest.json                 # 更新清单（由 CI 自动维护）
└── .github/workflows/build.yml # 打 tag 自动发版
```

---

## 常见问题

**双击没反应？** UAC 点了“否”。

**托盘找不到图标？** 默认收在「^」溢出区。

**开机后图标闪一下消失？** 正常：两个应用都没启用「容量上限清理」时，开机只提示一下然后退出。

**清理会不会丢聊天记录？** 不会。只删本地缓存文件，不动数据库；旧图片点开时客户端会重新下载
（受漫游有效期限制）。

---

## License

MIT，见 [LICENSE](LICENSE)。
