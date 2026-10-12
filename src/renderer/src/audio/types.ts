export type ChannelId = 'loopback' | 'mic' | 'effect' | 'teammates' | 'monitor'

export interface DeviceOption {
  deviceId: string
  label: string
  kind: 'audioinput' | 'audiooutput'
  virtual: boolean
}

export type LevelSnapshot = Record<ChannelId, number>

export type LogLevel = 'info' | 'warn' | 'error'

export interface LogEntry {
  level: LogLevel
  at: number
  text: string
}

export const EMPTY_LEVELS: LevelSnapshot = {
  loopback: 0,
  mic: 0,
  effect: 0,
  teammates: 0,
  monitor: 0
}
