import { captureMicrophone, captureSystemLoopback, describeAudioTrack } from './loopback'
import type { LoopbackMethod } from './loopback'
import { EMPTY_LEVELS } from './types'
import type { LevelSnapshot, LogLevel } from './types'

export interface StartOptions {
  inputDeviceId: string
  teammatesDeviceId: string
  monitorDeviceId: string
  useLegacyCapture: boolean
}

export interface EngineEvents {
  log: (level: LogLevel, text: string) => void
  levels: (levels: LevelSnapshot) => void
}

interface OutputRoute {
  label: string
  element: HTMLAudioElement
  destination: MediaStreamAudioDestinationNode
  sinkId: string
}

type SinkCapable = HTMLMediaElement & { setSinkId?: (deviceId: string) => Promise<void> }

const CHANNELS: Array<keyof LevelSnapshot> = ['loopback', 'mic', 'effect', 'teammates', 'monitor']
const METER_INTERVAL_MS = 100
const SILENCE_THRESHOLD = 0.0015
const SILENCE_WARN_TICKS = 100

/**
 * 混音图（麦克风 / 回环 / 音效各一路，分别有送队友和本地监听两个推子）：
 *
 *   麦克风 ──┬─→ 送队友 ──→ 限制器 ──→ CABLE-B
 *            └─→ 本地监听 ─────────────→ 物理耳机
 *   回环   ──┬─→ 送队友
 *            └─→ 本地监听
 *   音效   ──┬─→ 送队友
 *            └─→ 本地监听
 *
 * 两路输出都通过 MediaStreamAudioDestinationNode 加 <audio setSinkId> 落地，
 * AudioContext 自己的 destination 全程不接任何东西。一旦接了，声音会流到
 * 系统默认设备（也就是被采集的那条虚拟线）上，形成回授。
 */
export class AudioEngine {
  private readonly events: EngineEvents
  private ctx: AudioContext | null = null
  private micStream: MediaStream | null = null
  private loopStream: MediaStream | null = null
  private loopMethod: LoopbackMethod | null = null
  private teammatesRoute: OutputRoute | null = null
  private monitorRoute: OutputRoute | null = null
  private effectIn: GainNode | null = null
  private readonly gains = new Map<string, GainNode>()
  private readonly analysers = new Map<string, AnalyserNode>()
  private readonly meters = new Map<string, Float32Array<ArrayBuffer>>()
  private readonly buffers = new Map<string, AudioBuffer>()
  private readonly labels = new Map<string, string>()
  private trackInfo: Record<string, unknown> = {}
  private meterTimer: number | null = null
  private logTicks = 0
  private silentTicks = 0
  private warnedSilence = false
  private running = false

  constructor(events: EngineEvents) {
    this.events = events
  }

  get isRunning(): boolean {
    return this.running
  }

  get baseLatencyMs(): number {
    return (this.ctx?.baseLatency ?? 0) * 1000
  }

  get outputLatencyMs(): number {
    const ctx = this.ctx as (AudioContext & { outputLatency?: number }) | null
    return (ctx?.outputLatency ?? 0) * 1000
  }

  get sampleRate(): number {
    return this.ctx?.sampleRate ?? 0
  }

  get contextState(): string {
    return this.ctx?.state ?? 'closed'
  }

  get captureMethod(): string {
    return this.loopMethod ?? '-'
  }

  get trackSettings(): Record<string, unknown> {
    return this.trackInfo
  }

  get effectNames(): string[] {
    return [...this.buffers.keys()]
  }

  setDeviceLabels(entries: Array<{ deviceId: string; label: string }>): void {
    entries.forEach((entry) => this.labels.set(entry.deviceId, entry.label))
  }

  async start(options: StartOptions): Promise<void> {
    await this.stop()
    this.events.log('info', '正在建立音频通路…')
    try {
      await this.setup(options)
    } catch (error) {
      const text = error instanceof Error ? error.message : String(error)
      this.events.log('error', `建立通路失败，已回收占用的设备：${text}`)
      await this.stop()
      throw error
    }
    this.events.log('info', '通路建立完成，开始每秒采样电平')
  }

  private async setup(options: StartOptions): Promise<void> {
    const ctx = new AudioContext({ latencyHint: 'interactive' })
    this.ctx = ctx
    await ctx.resume()
    this.events.log(
      'info',
      `AudioContext 已启动：${ctx.sampleRate}Hz，baseLatency ${this.baseLatencyMs.toFixed(1)}ms`
    )

    const loopback = await captureSystemLoopback(options.useLegacyCapture)
    this.loopStream = loopback.stream
    this.loopMethod = loopback.method
    this.events.log(
      'info',
      `回环采集成功（${loopback.method}）：视频轨 ${loopback.videoTracks} 条已降到 1fps`
    )

    this.micStream = await captureMicrophone(options.inputDeviceId)
    this.events.log(
      'info',
      `麦克风已就绪：${this.micStream.getAudioTracks()[0]?.label || '(未命名设备)'}`
    )

    this.teammatesRoute = await this.createRoute('送队友', options.teammatesDeviceId)
    this.monitorRoute = options.monitorDeviceId
      ? await this.createRoute('本地监听', options.monitorDeviceId)
      : null
    if (!this.monitorRoute) {
      this.events.log('warn', '没有选择本地监听设备，你自己将听不到音乐和音效')
    }

    this.buildGraph()
    this.watchTracks()
    this.trackInfo = this.collectTrackInfo()
    this.buffers.set('测试音 440Hz', this.createTone(440, 400))
    this.buffers.set('脉冲(测延迟)', this.createImpulse())
    this.warnAboutDeviceChoices(options)

    this.running = true
    this.logTicks = 0
    this.silentTicks = 0
    this.warnedSilence = false
    this.meterTimer = window.setInterval(() => this.tick(), METER_INTERVAL_MS)
  }

  async stop(): Promise<void> {
    this.running = false
    if (this.meterTimer !== null) {
      window.clearInterval(this.meterTimer)
      this.meterTimer = null
    }
    for (const stream of [this.micStream, this.loopStream]) {
      stream?.getTracks().forEach((track) => track.stop())
    }
    this.micStream = null
    this.loopStream = null
    for (const route of [this.teammatesRoute, this.monitorRoute]) {
      if (!route) continue
      route.element.pause()
      route.element.srcObject = null
      route.element.remove()
    }
    this.teammatesRoute = null
    this.monitorRoute = null
    this.gains.clear()
    this.analysers.clear()
    this.meters.clear()
    this.buffers.clear()
    this.effectIn = null
    this.trackInfo = {}
    this.loopMethod = null
    if (this.ctx) {
      const ctx = this.ctx
      this.ctx = null
      await ctx.close().catch(() => undefined)
    }
    this.events.levels({ ...EMPTY_LEVELS })
  }

  gainValue(name: string): number {
    return this.gains.get(name)?.gain.value ?? 0
  }

  setGain(name: string, value: number): void {
    const node = this.gains.get(name)
    if (!node || !this.ctx) return
    node.gain.setTargetAtTime(value, this.ctx.currentTime, 0.01)
  }

  async addEffectFromFile(file: File): Promise<void> {
    const ctx = this.ctx
    if (!ctx) throw new Error('请先启动通路')
    const data = await file.arrayBuffer()
    const buffer = await ctx.decodeAudioData(data)
    this.buffers.set(file.name, buffer)
    this.events.log(
      'info',
      `已载入音效 ${file.name}：${buffer.duration.toFixed(2)}s / ${buffer.sampleRate}Hz / ${buffer.numberOfChannels}ch`
    )
  }

  playEffect(name: string): void {
    const ctx = this.ctx
    const buffer = this.buffers.get(name)
    if (!ctx || !buffer || !this.effectIn) {
      this.events.log('warn', `音效「${name}」还没准备好`)
      return
    }
    const source = ctx.createBufferSource()
    source.buffer = buffer
    source.connect(this.effectIn)
    source.start()
    this.events.log('info', `播放音效：${name}`)
  }

  describe(): Record<string, unknown> {
    const gains: Record<string, number> = {}
    this.gains.forEach((node, name) => {
      gains[name] = Number(node.gain.value.toFixed(3))
    })
    return {
      captureMethod: this.loopMethod ?? null,
      contextState: this.contextState,
      sampleRate: this.sampleRate,
      baseLatencyMs: Number(this.baseLatencyMs.toFixed(2)),
      outputLatencyMs: Number(this.outputLatencyMs.toFixed(2)),
      teammatesSink: this.teammatesRoute ? this.label(this.teammatesRoute.sinkId) : null,
      monitorSink: this.monitorRoute ? this.label(this.monitorRoute.sinkId) : null,
      gains,
      tracks: this.trackInfo
    }
  }

  private buildGraph(): void {
    const ctx = this.ctx
    if (!ctx || !this.micStream || !this.loopStream || !this.teammatesRoute) return

    const micAnalyser = this.analyser('mic', ctx.createMediaStreamSource(this.micStream))
    const loopAnalyser = this.analyser('loopback', ctx.createMediaStreamSource(this.loopStream))
    this.effectIn = this.gain('effect-in', 1)
    const effectAnalyser = this.analyser('effect', this.effectIn)

    const micToTeammates = this.gain('mic->teammates', 1)
    const micToMonitor = this.gain('mic->monitor', 0)
    const loopToTeammates = this.gain('loop->teammates', 0.7)
    const loopToMonitor = this.gain('loop->monitor', 0.8)
    const effectToTeammates = this.gain('effect->teammates', 0.9)
    const effectToMonitor = this.gain('effect->monitor', 0.8)

    const teammatesMix = ctx.createGain()
    const monitorMix = ctx.createGain()

    micAnalyser.connect(micToTeammates)
    micAnalyser.connect(micToMonitor)
    loopAnalyser.connect(loopToTeammates)
    loopAnalyser.connect(loopToMonitor)
    effectAnalyser.connect(effectToTeammates)
    effectAnalyser.connect(effectToMonitor)
    micToTeammates.connect(teammatesMix)
    loopToTeammates.connect(teammatesMix)
    effectToTeammates.connect(teammatesMix)
    micToMonitor.connect(monitorMix)
    loopToMonitor.connect(monitorMix)
    effectToMonitor.connect(monitorMix)

    // 麦克风 + 音乐 + 音效叠起来容易削顶，送队友那路挂个限制器兜底
    const limiter = ctx.createDynamicsCompressor()
    limiter.threshold.value = -3
    limiter.knee.value = 0
    limiter.ratio.value = 20
    limiter.attack.value = 0.003
    limiter.release.value = 0.25

    const teammatesAnalyser = this.analyser('teammates', teammatesMix)
    teammatesAnalyser.connect(limiter)
    limiter.connect(this.teammatesRoute.destination)
    const monitorAnalyser = this.analyser('monitor', monitorMix)
    if (this.monitorRoute) {
      monitorAnalyser.connect(this.monitorRoute.destination)
    }
  }

  private async createRoute(label: string, deviceId: string): Promise<OutputRoute> {
    const ctx = this.ctx
    if (!ctx) throw new Error('音频上下文还没准备好')
    const destination = ctx.createMediaStreamDestination()
    const element = document.createElement('audio')
    element.srcObject = destination.stream
    element.style.display = 'none'
    document.body.appendChild(element)
    await element.play().catch((error: unknown) => {
      this.events.log('error', `${label}播放器启动失败：${String(error)}`)
    })
    let sinkId = ''
    if (deviceId) {
      await applySinkId(element, deviceId)
      sinkId = deviceId
    }
    this.events.log('info', `${label} → ${deviceId ? this.label(deviceId) : '系统默认设备'}`)
    return { label, element, destination, sinkId }
  }

  private gain(name: string, value: number): GainNode {
    const ctx = this.ctx as AudioContext
    const node = ctx.createGain()
    node.gain.value = value
    this.gains.set(name, node)
    return node
  }

  private analyser(name: string, input: AudioNode): AnalyserNode {
    const ctx = this.ctx as AudioContext
    const node = ctx.createAnalyser()
    node.fftSize = 1024
    input.connect(node)
    this.analysers.set(name, node)
    this.meters.set(name, new Float32Array(node.fftSize))
    return node
  }

  private watchTracks(): void {
    const watch = (stream: MediaStream | null, who: string): void => {
      stream?.getTracks().forEach((track) => {
        track.addEventListener('ended', () =>
          this.events.log('error', `${who}的轨道被终止（${track.kind}），通路已断`)
        )
        track.addEventListener('mute', () => this.events.log('warn', `${who}的轨道进入静音状态`))
        track.addEventListener('unmute', () => this.events.log('info', `${who}的轨道恢复`))
      })
    }
    watch(this.loopStream, '回环采集')
    watch(this.micStream, '麦克风')
  }

  private collectTrackInfo(): Record<string, unknown> {
    const info: Record<string, unknown> = {}
    if (this.loopStream) {
      info.loopback = this.loopStream.getAudioTracks().map(describeAudioTrack)
    }
    if (this.micStream) {
      info.microphone = this.micStream.getAudioTracks().map(describeAudioTrack)
    }
    return info
  }

  private tick(): void {
    if (!this.running) return
    const levels = this.readLevels()
    this.events.levels(levels)
    this.logTicks += 1
    if (levels.loopback < SILENCE_THRESHOLD) {
      this.silentTicks += 1
    } else {
      this.silentTicks = 0
      this.warnedSilence = false
    }
    if (this.logTicks * METER_INTERVAL_MS >= 1000) {
      this.logTicks = 0
      this.events.log(
        'info',
        `[采样] 回环 ${this.db(levels.loopback)} / 麦克风 ${this.db(levels.mic)} / 送队友 ${this.db(
          levels.teammates
        )} / 监听 ${this.db(levels.monitor)} | ctx=${this.contextState} | 静音 ${(
          this.silentTicks / 10
        ).toFixed(0)}s`
      )
    }
    if (this.silentTicks >= SILENCE_WARN_TICKS && !this.warnedSilence) {
      this.warnedSilence = true
      this.events.log(
        'warn',
        '回环连续 10 秒没有信号：确认默认播放设备已切到虚拟线，且音乐正在播放'
      )
    }
  }

  private readLevels(): LevelSnapshot {
    const result = { ...EMPTY_LEVELS }
    CHANNELS.forEach((channel) => {
      result[channel] = this.rms(channel)
    })
    return result
  }

  private rms(channel: keyof LevelSnapshot): number {
    const analyser = this.analysers.get(channel)
    const data = this.meters.get(channel)
    if (!analyser || !data) return 0
    analyser.getFloatTimeDomainData(data)
    let sum = 0
    for (let i = 0; i < data.length; i += 1) {
      sum += data[i] * data[i]
    }
    return Math.sqrt(sum / data.length)
  }

  private db(value: number): string {
    if (value <= 0.00001) return '-∞'
    return `${(20 * Math.log10(value)).toFixed(1)}dB`
  }

  private createTone(frequency: number, ms: number): AudioBuffer {
    const ctx = this.ctx as AudioContext
    const length = Math.floor((ms / 1000) * ctx.sampleRate)
    const buffer = ctx.createBuffer(1, length, ctx.sampleRate)
    const data = buffer.getChannelData(0)
    const attack = Math.max(1, Math.floor(0.005 * ctx.sampleRate))
    const release = Math.max(1, Math.floor(0.02 * ctx.sampleRate))
    for (let i = 0; i < length; i += 1) {
      const envelope = Math.min(1, i / attack) * Math.min(1, Math.max(0, length - i) / release)
      data[i] = Math.sin((2 * Math.PI * frequency * i) / ctx.sampleRate) * envelope * 0.6
    }
    return buffer
  }

  private createImpulse(): AudioBuffer {
    const ctx = this.ctx as AudioContext
    const length = Math.floor(0.05 * ctx.sampleRate)
    const buffer = ctx.createBuffer(1, length, ctx.sampleRate)
    buffer.getChannelData(0)[0] = 1
    return buffer
  }

  private warnAboutDeviceChoices(options: StartOptions): void {
    if (!options.teammatesDeviceId) {
      this.events.log('warn', '送队友用的是系统默认设备，也就是被采集的那条线，会形成回授')
    }
    const monitor = this.label(options.monitorDeviceId)
    if (
      this.monitorRoute &&
      /(cable|voicemeeter|vb-audio|virtual|blackhole|loopback)/i.test(monitor)
    ) {
      this.events.log(
        'warn',
        `本地监听指向了虚拟设备「${monitor}」，声音会被自己采回去，建议改成物理耳机`
      )
    }
  }

  private label(deviceId: string): string {
    if (!deviceId) return ''
    return this.labels.get(deviceId) ?? deviceId.slice(0, 12)
  }
}

export async function applySinkId(element: HTMLMediaElement, deviceId: string): Promise<void> {
  const sinkCapable = element as SinkCapable
  if (typeof sinkCapable.setSinkId !== 'function') {
    throw new Error('当前运行时没有 setSinkId，无法指定输出设备')
  }
  await sinkCapable.setSinkId(deviceId)
}
