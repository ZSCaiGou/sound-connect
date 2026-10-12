import { ElectronAPI } from '@electron-toolkit/preload'

export interface SoundConnectApi {
  getDesktopSources: () => Promise<Array<{ id: string; name: string }>>
  copyText: (text: string) => Promise<void>
}

declare global {
  interface Window {
    electron: ElectronAPI
    api: SoundConnectApi
  }
}
