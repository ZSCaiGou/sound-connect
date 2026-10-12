import type { DeviceOption } from './types'

const VIRTUAL_PATTERN =
  /(cable|voicemeeter|vb-audio|virtual|blackhole|soundpad|loopback|voice ?meeter)/i

export function looksVirtual(label: string): boolean {
  return VIRTUAL_PATTERN.test(label)
}

export async function listAudioDevices(): Promise<DeviceOption[]> {
  const devices = await navigator.mediaDevices.enumerateDevices()
  return devices
    .filter((device) => device.kind === 'audioinput' || device.kind === 'audiooutput')
    .map((device) => {
      const isInput = device.kind === 'audioinput'
      return {
        deviceId: device.deviceId,
        label: device.label || (isInput ? '(未授权的输入设备)' : '(未授权的输出设备)'),
        kind: device.kind as 'audioinput' | 'audiooutput',
        virtual: looksVirtual(device.label)
      }
    })
}

/**
 * 请求一次麦克风权限。浏览器在授权前不会给出设备名称，标签为空时无法判断哪条是虚拟线。
 */
export async function requestLabelPermission(): Promise<void> {
  const stream = await navigator.mediaDevices.getUserMedia({ audio: true })
  stream.getTracks().forEach((track) => track.stop())
}
