# sound-connect

基于 Electron + React + TypeScript 的跨平台桌面应用，使用 [electron-vite](https://electron-vite.org) 构建。

## 技术栈

- **Electron 39** — 桌面运行时
- **React 19 + TypeScript 5.9** — 渲染层
- **electron-vite 5（Vite 7）** — 开发服务器与构建
- **electron-builder 26** — 打包与分发

## 环境要求

- Node.js ≥ 22
- 包管理器：[pnpm](https://pnpm.io)

## 常用命令

```bash
pnpm install      # 安装依赖
pnpm dev          # 启动开发环境（主进程热重载 + 渲染层 HMR）
pnpm typecheck    # 类型检查（主进程 / 渲染层）
pnpm lint         # ESLint 检查
pnpm format       # Prettier 格式化
pnpm build        # 类型检查 + 构建
pnpm build:win    # 打包 Windows
pnpm build:mac    # 打包 macOS
pnpm build:linux  # 打包 Linux
```

## 目录结构

```text
src
├── main        # Electron 主进程（窗口、IPC、系统能力）
├── preload     # 预加载脚本（contextBridge 暴露给渲染层的 API）
└── renderer    # React 渲染层
build           # 打包资源（图标、entitlements 等）
resources       # 运行时资源
```

## 相关链接

- 仓库：<https://github.com/ZSCaiGou/sound-connect>
