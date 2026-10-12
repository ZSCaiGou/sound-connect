export type LoopbackMethod = 'getDisplayMedia' | 'legacy-constraints'

export interface LoopbackResult {
  stream: MediaStream
  method: LoopbackMethod
  videoTracks: number
}

const PROCESSING_OFF: MediaTrackConstraints = {
  echoCancellation: false,
  noiseSuppression: false,
  autoGainControl: false
}

async function relaxProcessing(stream: MediaStream): Promise<void> {
  for (const track of stream.getAudioTracks()) {
    try {
      await track.applyConstraints(PROCESSING_OFF)
    } catch {
      // 有些实现不允许采集后修改，忽略。真实值请以 getSettings() 为准。
    }
  }
}

async function relaxVideo(stream: MediaStream): Promise<number> {
  const tracks = stream.getVideoTracks()
  for (const track of tracks) {
    try {
      await track.applyConstraints({ frameRate: 1 })
    } catch {
      // 视频轨降到 1fps 失败无所谓，只影响一点 CPU
    }
  }
  return tracks.length
}

/**
 * 采集系统默认播放设备的整路声音（Windows 上是 WASAPI 回环）。
 * 新接口走 getDisplayMedia + 主进程的 setDisplayMediaRequestHandler，
 * 旧接口走 chromeMediaSource 约束，Electron 改 API 时可以互为兜底。
 */
export async function captureSystemLoopback(useLegacy: boolean): Promise<LoopbackResult> {
  if (!useLegacy) {
    const stream = await navigator.mediaDevices.getDisplayMedia({
      video: true,
      audio: PROCESSING_OFF
    })
    await relaxProcessing(stream)
    const videoTracks = await relaxVideo(stream)
    return { stream, method: 'getDisplayMedia', videoTracks }
  }

  const sources = await window.api.getDesktopSources()
  if (sources.length === 0) {
    throw new Error('没有拿到可采集的屏幕源')
  }
  const sourceId = sources[0].id
  const constraints = {
    audio: {
      mandatory: {
        chromeMediaSource: 'desktop',
        chromeMediaSourceId: sourceId,
        echoCancellation: false,
        noiseSuppression: false,
        autoGainControl: false
      }
    },
    video: {
      mandatory: {
        chromeMediaSource: 'desktop',
        chromeMediaSourceId: sourceId,
        maxWidth: 2,
        maxHeight: 2,
        maxFrameRate: 1
      }
    }
  } as unknown as MediaStreamConstraints

  const stream = await navigator.mediaDevices.getUserMedia(constraints)
  await relaxProcessing(stream)
  const videoTracks = await relaxVideo(stream)
  return { stream, method: 'legacy-constraints', videoTracks }
}

export async function captureMicrophone(deviceId: string): Promise<MediaStream> {
  return navigator.mediaDevices.getUserMedia({
    audio: {
      deviceId: deviceId ? { exact: deviceId } : undefined,
      channelCount: 1,
      echoCancellation: false,
      noiseSuppression: false,
      autoGainControl: false
    }
  })
}

/**
 * 采集轨道的真实参数。验证清单第 2 条靠它：回声消除、降噪、自动增益到底关掉没有。
 */
export function describeAudioTrack(track: MediaStreamTrack): Record<string, unknown> {
  const settings = track.getSettings() as Record<string, unknown>
  return {
    label: track.label,
    readyState: track.readyState,
    muted: track.muted,
    sampleRate: settings.sampleRate ?? null,
    channelCount: settings.channelCount ?? null,
    deviceId: settings.deviceId ?? null,
    latency: settings.latency ?? null,
    echoCancellation: settings.echoCancellation ?? null,
    noiseSuppression: settings.noiseSuppression ?? null,
    autoGainControl: settings.autoGainControl ?? null
  }
}
