import { useCallback, useEffect, useMemo, useState } from 'react'
import { AudioEngine } from '../audio/engine'
import { listAudioDevices, requestLabelPermission } from '../audio/devices'
import { EMPTY_LEVELS } from '../audio/types'
import type { DeviceOption, LevelSnapshot, LogEntry, LogLevel } from '../audio/types'

interface GainRow {
  name: string
  label: string
  bus: '送队友' | '本地监听'
}

const GAIN_ROWS: GainRow[] = [
  { name: 'mic->teammates', label: '麦克风', bus: '送队友' },
  { name: 'loop->teammates', label: '音乐（回环）', bus: '送队友' },
  { name: 'effect->teammates', label: '音效', bus: '送队友' },
  { name: 'mic->monitor', label: '麦克风', bus: '本地监听' },
  { name: 'loop->monitor', label: '音乐（回环）', bus: '本地监听' },
  { name: 'effect->monitor', label: '音效', bus: '本地监听' }
]

const DEFAULT_GAINS: Record<string, number> = {
  'mic->teammates': 1,
  'loop->teammates': 0.7,
  'effect->teammates': 0.9,
  'mic->monitor': 0,
  'loop->monitor': 0.8,
  'effect->monitor': 0.8
}

const METERS: Array<{ key: keyof LevelSnapshot; label: string }> = [
  { key: 'loopback', label: '回环（音乐）' },
  { key: 'mic', label: '麦克风' },
  { key: 'effect', label: '音效' },
  { key: 'teammates', label: '送队友总输出' },
  { key: 'monitor', label: '本地监听总输出' }
]

function formatClock(at: number): string {
  const date = new Date(at)
  return [date.getHours(), date.getMinutes(), date.getSeconds()]
    .map((part) => String(part).padStart(2, '0'))
    .join(':')
}

function formatDb(value: number): string {
  if (value <= 0.00001) return '-∞'
  return `${(20 * Math.log10(value)).toFixed(0)} dB`
}

function Meter({ label, value }: { label: string; value: number }): React.JSX.Element {
  const percent = Math.min(100, Math.round(value * 260))
  return (
    <div className="meter">
      <span className="meter-label">{label}</span>
      <span className={`meter-bar${value > 0.002 ? ' active' : ''}`}>
        <span style={{ width: `${percent}%` }} />
      </span>
      <span className="meter-value">{formatDb(value)}</span>
    </div>
  )
}

function DeviceSelect(props: {
  label: string
  hint: string
  value: string
  options: DeviceOption[]
  allowEmpty?: boolean
  emptyLabel?: string
  onChange: (value: string) => void
}): React.JSX.Element {
  const { label, hint, value, options, allowEmpty, emptyLabel, onChange } = props
  return (
    <label className="field">
      <span className="field-label">
        {label}
        <em>{hint}</em>
      </span>
      <select value={value} onChange={(event) => onChange(event.target.value)}>
        {allowEmpty ? <option value="">{emptyLabel ?? '（不使用）'}</option> : null}
        {options.map((option) => (
          <option key={option.deviceId} value={option.deviceId}>
            {option.virtual ? '◆ ' : ''}
            {option.label}
          </option>
        ))}
      </select>
    </label>
  )
}

function SpikePanel(): React.JSX.Element {
  const [devices, setDevices] = useState<DeviceOption[]>([])
  const [inputId, setInputId] = useState('')
  const [teammatesId, setTeammatesId] = useState('')
  const [monitorId, setMonitorId] = useState('')
  const [useLegacy, setUseLegacy] = useState(false)
  const [running, setRunning] = useState(false)
  const [busy, setBusy] = useState(false)
  const [levels, setLevels] = useState<LevelSnapshot>({ ...EMPTY_LEVELS })
  const [gains, setGains] = useState<Record<string, number>>({ ...DEFAULT_GAINS })
  const [logs, setLogs] = useState<LogEntry[]>([])
  const [effects, setEffects] = useState<string[]>([])
  const [copied, setCopied] = useState(false)
  const appendLog = useCallback((level: LogLevel, text: string) => {
    setLogs((previous) => {
      const entry: LogEntry = { level, at: Date.now(), text }
      const next = [...previous, entry]
      return next.length > 800 ? next.slice(next.length - 800) : next
    })
  }, [])

  const [engine] = useState(
    () =>
      new AudioEngine({
        log: appendLog,
        levels: (snapshot) => setLevels(snapshot)
      })
  )

  const applyDeviceList = useCallback(
    (list: DeviceOption[]): void => {
      setDevices(list)
      engine.setDeviceLabels(list.map((item) => ({ deviceId: item.deviceId, label: item.label })))
      setInputId(
        (current) => current || (list.find((item) => item.kind === 'audioinput')?.deviceId ?? '')
      )
      setTeammatesId(
        (current) =>
          current ||
          (list.find((item) => item.kind === 'audiooutput' && item.virtual)?.deviceId ?? '')
      )
      setMonitorId(
        (current) =>
          current ||
          (list.find((item) => item.kind === 'audiooutput' && !item.virtual)?.deviceId ?? '')
      )
    },
    [engine]
  )

  const refreshDevices = useCallback(async (): Promise<void> => {
    applyDeviceList(await listAudioDevices())
  }, [applyDeviceList])

  useEffect(() => {
    let cancelled = false
    void listAudioDevices().then((list) => {
      if (!cancelled) applyDeviceList(list)
    })
    return () => {
      cancelled = true
      void engine.stop()
    }
  }, [applyDeviceList, engine])

  const inputs = useMemo(() => devices.filter((item) => item.kind === 'audioinput'), [devices])
  const outputs = useMemo(() => devices.filter((item) => item.kind === 'audiooutput'), [devices])
  const virtualCount = useMemo(() => outputs.filter((item) => item.virtual).length, [outputs])

  const start = useCallback(async (): Promise<void> => {
    setBusy(true)
    try {
      await engine.start({
        inputDeviceId: inputId,
        teammatesDeviceId: teammatesId,
        monitorDeviceId: monitorId,
        useLegacyCapture: useLegacy
      })
      setEffects(engine.effectNames)
      setRunning(true)
    } catch (error) {
      appendLog('error', `启动失败：${error instanceof Error ? error.message : String(error)}`)
    } finally {
      setBusy(false)
    }
  }, [appendLog, engine, inputId, monitorId, teammatesId, useLegacy])

  const stop = useCallback(async (): Promise<void> => {
    await engine.stop()
    setEffects([])
    setRunning(false)
  }, [engine])

  const changeGain = useCallback(
    (name: string, value: number): void => {
      setGains((previous) => ({ ...previous, [name]: value }))
      engine.setGain(name, value)
    },
    [engine]
  )

  const loadFiles = useCallback(
    async (files: FileList | null): Promise<void> => {
      if (!files) return
      for (const file of Array.from(files)) {
        try {
          await engine.addEffectFromFile(file)
        } catch (error) {
          appendLog('error', `载入 ${file.name} 失败：${String(error)}`)
        }
      }
      setEffects(engine.effectNames)
    },
    [appendLog, engine]
  )

  const buildReport = useCallback((): string => {
    return [
      '=== sound-connect 验证原型报告 ===',
      `时间：${new Date().toLocaleString()}`,
      `采集接口：${engine.captureMethod}`,
      `输入设备：${inputs.find((item) => item.deviceId === inputId)?.label ?? inputId}`,
      `送队友设备：${outputs.find((item) => item.deviceId === teammatesId)?.label ?? teammatesId}`,
      `本地监听设备：${outputs.find((item) => item.deviceId === monitorId)?.label ?? monitorId}`,
      '',
      '--- 设备列表 ---',
      ...outputs.map((item) => `${item.virtual ? '[虚拟]' : '[物理]'} ${item.label}`),
      '',
      '--- 诊断 JSON ---',
      JSON.stringify(engine.describe(), null, 2),
      '',
      '--- 日志 ---',
      ...logs.map((entry) => `${formatClock(entry.at)} [${entry.level}] ${entry.text}`)
    ].join('\n')
  }, [engine, inputId, inputs, logs, monitorId, outputs, teammatesId])

  const copyReport = useCallback(async (): Promise<void> => {
    await window.api.copyText(buildReport())
    setCopied(true)
    window.setTimeout(() => setCopied(false), 1500)
  }, [buildReport])

  return (
    <div className="app">
      <header className="app-header">
        <div>
          <h1>sound-connect 验证原型</h1>
          <p>
            目标：验证回环采集、双输出、延迟。开始前请确认系统默认播放设备已切到虚拟线（如
            CABLE-A）， Discord 的播放设备单独指向物理耳机。
          </p>
        </div>
        <div className="app-actions">
          {running ? (
            <button className="danger" onClick={() => void stop()}>
              停止通路
            </button>
          ) : (
            <button disabled={busy} onClick={() => void start()}>
              {busy ? '正在建立…' : '建立通路'}
            </button>
          )}
        </div>
      </header>

      <section className="card">
        <h2>1. 设备</h2>
        <div className="grid">
          <DeviceSelect
            label="麦克风"
            hint="输入"
            value={inputId}
            options={inputs}
            onChange={setInputId}
          />
          <DeviceSelect
            label="送队友"
            hint="输出到 CABLE-B"
            value={teammatesId}
            options={outputs}
            allowEmpty
            emptyLabel="（系统默认，勿选）"
            onChange={setTeammatesId}
          />
          <DeviceSelect
            label="本地监听"
            hint="输出到物理耳机"
            value={monitorId}
            options={outputs}
            allowEmpty
            emptyLabel="（不监听）"
            onChange={setMonitorId}
          />
        </div>
        <div className="row">
          <button className="ghost" onClick={() => void refreshDevices()}>
            刷新设备
          </button>
          <button
            className="ghost"
            onClick={() =>
              void requestLabelPermission()
                .then(() => refreshDevices())
                .catch((error: unknown) => appendLog('error', `授权失败：${String(error)}`))
            }
          >
            授权并刷新
          </button>
          <label className="inline">
            <input
              type="checkbox"
              checked={useLegacy}
              onChange={(e) => setUseLegacy(e.target.checked)}
            />
            用旧接口采集（getDisplayMedia 失败时勾上）
          </label>
          <span className="hint">◆ 标记的是疑似虚拟设备，共 {virtualCount} 个</span>
        </div>
      </section>

      <section className="card">
        <h2>2. 电平</h2>
        <div className="meters">
          {METERS.map((meter) => (
            <Meter key={meter.key} label={meter.label} value={levels[meter.key]} />
          ))}
        </div>
      </section>

      <section className="card">
        <h2>3. 音量推子</h2>
        <div className="grid">
          {(['送队友', '本地监听'] as const).map((bus) => (
            <div key={bus} className="group">
              <h3>{bus}</h3>
              {GAIN_ROWS.filter((row) => row.bus === bus).map((row) => (
                <label key={row.name} className="slider">
                  <span className="slider-label">{row.label}</span>
                  <input
                    type="range"
                    min={0}
                    max={1.5}
                    step={0.05}
                    value={gains[row.name] ?? 0}
                    onChange={(event) => changeGain(row.name, Number(event.target.value))}
                  />
                  <span className="slider-value">{(gains[row.name] ?? 0).toFixed(2)}</span>
                </label>
              ))}
            </div>
          ))}
        </div>
        <p className="hint">
          把「本地监听 → 麦克风」推上去，戴着耳机说话，能直接体感麦克风这条路的延迟。
        </p>
      </section>

      <section className="card">
        <h2>4. 音效</h2>
        <div className="row">
          {effects.map((name) => (
            <button
              key={name}
              className="ghost"
              disabled={!running}
              onClick={() => engine.playEffect(name)}
            >
              ▶ {name}
            </button>
          ))}
          {effects.length === 0 ? (
            <span className="hint">建立通路后可播放内置测试音和脉冲</span>
          ) : null}
        </div>
        <div className="row">
          <input
            type="file"
            accept="audio/*"
            multiple
            onChange={(event) => void loadFiles(event.target.files)}
          />
        </div>
      </section>

      <section className="card">
        <h2>5. 延迟</h2>
        <table className="table">
          <tbody>
            <tr>
              <td>采样率</td>
              <td>{engine.sampleRate || '-'} Hz</td>
            </tr>
            <tr>
              <td>baseLatency</td>
              <td>{running ? `${engine.baseLatencyMs.toFixed(2)} ms` : '-'}</td>
            </tr>
            <tr>
              <td>outputLatency</td>
              <td>{running ? `${engine.outputLatencyMs.toFixed(2)} ms` : '-'}</td>
            </tr>
            <tr>
              <td>采集接口</td>
              <td>{engine.captureMethod}</td>
            </tr>
            <tr>
              <td>采集轨参数</td>
              <td>
                <pre>{JSON.stringify(engine.trackSettings, null, 2)}</pre>
              </td>
            </tr>
          </tbody>
        </table>
        <p className="hint">
          真实端到端延迟请在 Discord 的「语音与视频 → 回声测试」里听自己的延迟，那是含 Discord
          的完整链路。
        </p>
      </section>

      <section className="card">
        <h2>6. 日志</h2>
        <div className="row">
          <button className="ghost" onClick={() => void copyReport()}>
            {copied ? '已复制' : '复制报告'}
          </button>
          <button className="ghost" onClick={() => setLogs([])}>
            清空
          </button>
        </div>
        <div className="log">
          {logs.map((entry, index) => (
            <div key={`${entry.at}-${index}`} className={`log-line ${entry.level}`}>
              <span className="log-time">{formatClock(entry.at)}</span>
              <span>{entry.text}</span>
            </div>
          ))}
          {logs.length === 0 ? <span className="hint">还没有日志</span> : null}
        </div>
      </section>
    </div>
  )
}

export default SpikePanel
