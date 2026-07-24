<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { ArrowLeft, Rocket, Play, Square, RotateCw, Download, Undo2, Save, CheckCircle2, TerminalSquare, Pencil, LoaderCircle, Trash2 } from 'lucide-vue-next'
import { api, localizeRuntimeLog, post, statusText, timeOnly, when } from '../api'
import { router, session } from '../main'
import { PageHeader } from '../components/Common.vue'

const route = useRoute()
const id = computed(() => String(route.params.id))
const tab = computed(() => String(route.params.tab || 'overview'))
const project = ref<any>(), tasks = ref<any[]>([]), releases = ref<any[]>([]), configs = ref<any[]>([])
const runtime = ref<any>({ state: 'NotRunning' }), runtimeLog = ref(''), selectedTask = ref<any>()
const logs = ref<any[]>([]), logContainer = ref<HTMLElement>(), logMode = ref<'connecting'|'live'|'polling'>('connecting')
const json = ref('{}'), comment = ref(''), revision = ref(''), error = ref(''), busy = ref(false), autoscroll = ref(true)
const downloadingReleaseId = ref('')
const downloadNotice = ref<{ type:'working'|'success'|'error', message:string } | null>(null)
const cleaningWorkspace = ref(false)
const workspaceNotice = ref<{ type:'success'|'error', message:string } | null>(null)
const tabs = [['overview','概况'],['release','发布中心'],['versions','版本历史'],['runtime','运行管理'],['config','配置中心']]
const LOG_PAGE_SIZE = 500, MAX_BROWSER_LOGS = 2000
let timer:any, logHub:any, connectedTaskId = '', lastLogSequence = 0
let refreshingLiveState = false

const stageSummary = computed(() => {
  const counts = new Map<string, number>()
  for (const item of logs.value) counts.set(item.stage || '其他', (counts.get(item.stage || '其他') || 0) + 1)
  return [...counts.entries()].map(([name, count]) => ({ name, count }))
})
const deploymentInProgress = computed(() => selectedTask.value && !['Succeeded','Failed','Cancelled','Interrupted','RolledBack'].includes(selectedTask.value.status))

async function scrollToLatest(force = false) {
  await nextTick()
  if ((force || autoscroll.value) && logContainer.value) logContainer.value.scrollTop = logContainer.value.scrollHeight
}

function appendLogs(incoming:any[]) {
  if (!incoming?.length) return
  const merged = new Map(logs.value.map(item => [item.sequence, item]))
  for (const item of incoming) merged.set(item.sequence, item)
  logs.value = [...merged.values()].sort((a:any,b:any) => a.sequence - b.sequence).slice(-MAX_BROWSER_LOGS)
  lastLogSequence = Math.max(lastLogSequence, ...incoming.map(x => Number(x.sequence || 0)))
  void scrollToLatest()
}

async function loadRecentLogs(taskId:string) {
  const recent:any[] = await api(`/deployments/${taskId}/logs?tail=true&limit=${LOG_PAGE_SIZE}`)
  logs.value = recent
  lastLogSequence = recent.length ? Math.max(...recent.map(x => Number(x.sequence))) : 0
  await scrollToLatest(true)
}

async function fetchLogsAfter(taskId:string, startAfter = lastLogSequence) {
  let after = startAfter
  for (let page = 0; page < 20; page++) {
    const items:any[] = await api(`/deployments/${taskId}/logs?after=${after}&limit=${LOG_PAGE_SIZE}`)
    appendLogs(items)
    if (!items.length || items.length < LOG_PAGE_SIZE) break
    after = Math.max(...items.map(x => Number(x.sequence)))
  }
}

async function stopLogStream() {
  connectedTaskId = ''
  if (logHub) { try { await logHub.stop() } catch {} logHub = null }
}

async function connectLogStream(taskId:string) {
  await stopLogStream()
  connectedTaskId = taskId
  logMode.value = 'connecting'
  const catchUpAfter = lastLogSequence
  const connection = new HubConnectionBuilder()
    .withUrl('/hubs/releases', { accessTokenFactory: () => session.token })
    .withAutomaticReconnect([0, 1000, 3000, 8000])
    .configureLogging(LogLevel.Warning)
    .build()
  logHub = connection
  connection.on('log', (item:any) => { if (item.taskId?.toLowerCase() === taskId.toLowerCase()) appendLogs([item]) })
  connection.onreconnecting(() => { logMode.value = 'connecting' })
  connection.onclose(() => { logMode.value = 'polling' })
  connection.onreconnected(async () => { await connection.invoke('JoinTask', taskId); await fetchLogsAfter(taskId); logMode.value = 'live' })
  try {
    await connection.start()
    await connection.invoke('JoinTask', taskId)
    await fetchLogsAfter(taskId, catchUpAfter)
    logMode.value = 'live'
  } catch {
    logMode.value = 'polling'
    try { await connection.stop() } catch {}
  }
}

async function selectTask(taskId:string) {
  selectedTask.value = await api(`/deployments/${taskId}`)
  await loadRecentLogs(taskId)
  await connectLogStream(taskId)
}

async function load() {
  try {
    error.value = ''
    project.value = await api(`/projects/${id.value}`)
    ;[tasks.value, releases.value, configs.value, runtime.value] = await Promise.all([
      api<any>(`/projects/${id.value}/deployments`).then(x => x.items), api(`/projects/${id.value}/releases`),
      api(`/projects/${id.value}/configurations`), api(`/projects/${id.value}/runtime`)
    ])
    if (configs.value[0] && !comment.value) { const active = configs.value.find(x => x.isActive) || configs.value[0]; json.value = active.jsonContent }
    const taskId = String(route.query.task || tasks.value[0]?.id || '')
    if (taskId && taskId !== connectedTaskId) await selectTask(taskId)
    else if (taskId) selectedTask.value = await api(`/deployments/${taskId}`)
    if (tab.value === 'runtime') runtimeLog.value = localizeRuntimeLog((await api<any>(`/projects/${id.value}/runtime/log`)).content)
  } catch (e:any) { error.value = e.message }
}

async function refreshLiveState() {
  if (refreshingLiveState) return
  refreshingLiveState = true
  try {
    if (tab.value === 'release' && selectedTask.value?.id) {
      selectedTask.value = await api(`/deployments/${selectedTask.value.id}`)
      const connected = logHub?.state === HubConnectionState.Connected
      if (!connected) logMode.value = 'polling'
      await fetchLogsAfter(selectedTask.value.id)
      if (connected) logMode.value = 'live'
    } else if (tab.value === 'runtime') {
      runtime.value = await api(`/projects/${id.value}/runtime`)
      runtimeLog.value = localizeRuntimeLog((await api<any>(`/projects/${id.value}/runtime/log`)).content)
    } else if (tab.value === 'overview') {
      ;[tasks.value, releases.value, runtime.value] = await Promise.all([api<any>(`/projects/${id.value}/deployments`).then(x => x.items), api(`/projects/${id.value}/releases`), api(`/projects/${id.value}/runtime`)])
    }
  } catch (e:any) { error.value = e.message }
  finally { refreshingLiveState = false }
}

async function deploy(){if(!confirm(`确认发布「${project.value.name}」${revision.value?'的 r'+revision.value:'的最新修订'}？`))return;busy.value=true;try{const r:any=await post(`/projects/${id.value}/deployments`,{revision:revision.value?Number(revision.value):null});await router.replace({path:`/projects/${id.value}/release`,query:{task:r.id}})}catch(e:any){alert(e.message)}finally{busy.value=false}}
async function cleanWorkspace() {
  if (cleaningWorkspace.value || deploymentInProgress.value) return
  if (!confirm(`确认清理「${project.value.name}」上一次下载的全部源码？\n\n下一次发布将重新执行完整 SVN checkout；已发布版本、配置和运行程序不会受影响。`)) return
  cleaningWorkspace.value = true
  workspaceNotice.value = null
  try {
    const result:any = await post(`/projects/${id.value}/workspace/clean`)
    const size = result.freedBytes ? formatBytes(result.freedBytes) : '0 B'
    workspaceNotice.value = { type: 'success', message: result.removed ? `源码已清理：删除 ${result.fileCount} 个文件，释放 ${size}。` : result.message }
  } catch (e:any) {
    workspaceNotice.value = { type: 'error', message: e.message }
  } finally {
    cleaningWorkspace.value = false
  }
}
function formatBytes(value:number) { const units=['B','KB','MB','GB','TB']; let size=value,index=0; while(size>=1024&&index<units.length-1){size/=1024;index++} return `${size.toFixed(index?1:0)} ${units[index]}` }
async function runtimeAction(action:string,force=false){if(!confirm(`确认对「${project.value.name}」执行${action==='start'?'启动':action==='stop'?'停止':'重启'}？`))return;busy.value=true;try{await post(`/projects/${id.value}/runtime/${action}${force?'?force=true':''}`);await refreshLiveState()}catch(e:any){alert(e.message)}finally{busy.value=false}}
async function saveConfig(){busy.value=true;try{await post(`/projects/${id.value}/configurations`,{jsonContent:json.value,comment:comment.value});comment.value='';await load()}catch(e:any){alert(e.message)}finally{busy.value=false}}
async function activate(c:any){if(confirm(`恢复配置版本 v${c.version}？`)){await post(`/projects/${id.value}/configurations/${c.id}/activate`);await load()}}
async function rollback(r:any){if(confirm(`确认将「${project.value.name}」回滚到 ${r.version} 并重启？`)){await post(`/projects/${id.value}/releases/${r.id}/rollback`);await load()}}
async function pkg(r:any) {
  if (downloadingReleaseId.value || !confirm(`发布包包含 appsettings.json，可能含敏感配置。确认生成并下载 ${r.version}？`)) return
  downloadingReleaseId.value = r.id
  downloadNotice.value = { type: 'working', message: `正在生成 ${r.version} 发布包，请稍候…` }
  try {
    const p:any = await post(`/releases/${r.id}/package`)
    downloadNotice.value = { type: 'working', message: `发布包已生成，正在准备下载 ${r.version}…` }
    const res = await fetch(`/api/packages/${p.id}/download`, { headers: { Authorization: `Bearer ${session.token}` } })
    if (!res.ok) throw new Error((await res.text()) || `下载请求失败（HTTP ${res.status}）`)
    const blob = await res.blob()
    if (!blob.size) throw new Error('服务器返回了空的发布包')
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = p.path.split(/[\\/]/).pop() || `${r.version}.zip`
    document.body.appendChild(a)
    a.click()
    a.remove()
    window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
    downloadNotice.value = { type: 'success', message: `${r.version} 发布包已生成，浏览器已开始下载。` }
  } catch (e:any) {
    downloadNotice.value = { type: 'error', message: `下载失败：${e.message || '未知错误'}` }
  } finally {
    downloadingReleaseId.value = ''
  }
}

watch(() => route.fullPath, load)
onMounted(async () => { await load(); timer = setInterval(refreshLiveState, 2500) })
onBeforeUnmount(() => { clearInterval(timer); stopLogStream() })
</script>
<template><PageHeader eyebrow="PROJECT WORKSPACE" :title="project?.name||'项目'" :description="project?.description||project?.svnUrl"><button class="btn ghost" @click="router.push('/')"><ArrowLeft :size="16"/>总览</button><button v-if="session.user?.role==='Admin'" class="btn ghost" @click="router.push(`/projects/${id}/edit`)"><Pencil :size="15"/>编辑</button></PageHeader><nav class="tabs"><RouterLink v-for="t in tabs" :key="t[0]" :to="`/projects/${id}/${t[0]}`">{{t[1]}}</RouterLink></nav><div v-if="error" class="alert error">{{error}}</div>
<section v-if="tab==='overview'" class="overview-grid"><article class="hero-status"><span class="eyebrow">CURRENT RELEASE</span><strong>{{releases.find(x=>x.id===project?.currentReleaseId)?.version||'尚未发布'}}</strong><p>SVN {{releases.find(x=>x.id===project?.currentReleaseId)?.svnRevision?'r'+releases.find(x=>x.id===project?.currentReleaseId)?.svnRevision:'—'}}</p><div :class="['runtime-orb',runtime.state==='Running'?'on':'']"><i></i>{{statusText(runtime.state)}}</div></article><article class="detail-card"><h3>构建坐标</h3><dl><dt>仓库</dt><dd>{{project?.svnUrl}}</dd><dt>后台项目</dt><dd>{{project?.backendDirectory}}/{{project?.backendProjectFile}}</dd><dt>管理端</dt><dd>{{project?.adminDirectory}} → {{project?.adminOutputDirectory}}</dd><dt>终端</dt><dd>{{project?.terminalDirectory}} → {{project?.terminalOutputDirectory}}</dd></dl></article><article class="detail-card activity"><h3>最近活动</h3><div v-for="t in tasks.slice(0,5)" :key="t.id"><i :class="t.status==='Succeeded'?'ok':''"></i><span>{{statusText(t.status)}}</span><small>{{when(t.createdAtUtc)}}</small></div></article></section>
<section v-if="tab==='release'" class="release-layout">
  <aside class="release-command">
    <span class="eyebrow">NEW DEPLOYMENT</span><h2>启动发布</h2>
    <p>留空使用 SVN 最新修订；填写数字可精确复现历史代码。</p>
    <label>SVN 修订号<input v-model="revision" type="number" placeholder="HEAD / 最新"/></label>
    <button class="btn primary wide" :disabled="busy||deploymentInProgress" @click="deploy"><Rocket :size="17"/>{{busy?'提交中…':'开始发布'}}</button>
    <button v-if="deploymentInProgress" class="btn danger wide" @click="post(`/deployments/${selectedTask.id}/cancel`)">取消任务</button>
    <div v-if="session.user?.role!=='Viewer'" class="workspace-maintenance">
      <span>WORKSPACE MAINTENANCE</span>
      <p>删除上次检出的 SVN 源码，下次发布将重新完整下载。</p>
      <button class="workspace-clean" :disabled="cleaningWorkspace||deploymentInProgress" :title="deploymentInProgress?'发布任务进行中，暂不能清理':'清理上一次下载的源码'" @click="cleanWorkspace"><LoaderCircle v-if="cleaningWorkspace" class="spin"/><Trash2 v-else/>{{cleaningWorkspace?'清理中…':'清理源码工作区'}}</button>
      <div v-if="workspaceNotice" :class="['workspace-notice',workspaceNotice.type]" role="status">{{workspaceNotice.message}}</div>
    </div>
  </aside>
  <div class="release-console">
    <div class="progress-head"><div><span>任务 {{selectedTask?.id?.slice(0,8)||'—'}}</span><strong>{{statusText(selectedTask?.status||'等待任务')}}</strong></div><b>{{selectedTask?.progress||0}}%</b></div>
    <div class="progress-track"><i :style="{width:`${selectedTask?.progress||0}%`}"></i></div>
    <div v-if="selectedTask?.error" class="alert error">{{selectedTask.error}}</div>
    <div class="console-head"><span><TerminalSquare :size="16"/>实时发布日志 <small :class="['log-mode',logMode]">{{logMode==='live'?'实时推送':logMode==='connecting'?'正在连接':'增量补拉'}}</small></span><div class="log-actions"><span>当前窗口 {{logs.length}} 条</span><label><input v-model="autoscroll" type="checkbox"/>自动滚动</label><button type="button" @click="scrollToLatest(true)">跳到最新</button></div></div>
    <div v-if="stageSummary.length" class="log-stage-strip" aria-label="当前日志窗口阶段统计">
      <span v-for="stage in stageSummary" :key="stage.name"><b>{{stage.name}}</b><small>{{stage.count}}</small></span>
    </div>
    <div ref="logContainer" class="console release-log">
      <div v-for="l in logs" :key="l.id" :class="['console-line', l.level.toLowerCase()]">
        <time>{{timeOnly(l.timestampUtc)}}</time>
        <b>{{l.stage}}</b>
        <strong v-if="l.level==='Command'" class="command-mark">CMD</strong>
        <code>{{l.message}}</code>
      </div>
      <em v-if="!logs.length">等待任务日志…</em>
    </div>
  </div>
</section>
<section v-if="tab==='versions'" class="table-card"><div class="table-title"><div><h3>成功版本</h3><p>不可变产物，可生成 ZIP 或回滚为当前版本。</p></div></div><div v-if="downloadNotice" :class="['download-notice',downloadNotice.type]" role="status" aria-live="polite"><LoaderCircle v-if="downloadNotice.type==='working'" class="spin"/><CheckCircle2 v-else-if="downloadNotice.type==='success'"/><span>{{downloadNotice.message}}</span><button type="button" aria-label="关闭提示" @click="downloadNotice=null">×</button></div><table><thead><tr><th>版本</th><th>Revision</th><th>配置</th><th>触发人</th><th>构建时间</th><th>摘要</th><th></th></tr></thead><tbody><tr v-for="r in releases" :key="r.id"><td><strong>{{r.version}}</strong><small v-if="r.id===project?.currentReleaseId" class="current-tag">当前</small></td><td>r{{r.svnRevision}}</td><td>v{{r.configurationVersion}}</td><td>{{r.triggeredByName}}</td><td>{{when(r.builtAtUtc)}}</td><td class="mono">{{r.manifestSha256.slice(0,10)}}…</td><td class="row-actions"><button class="download-action" :disabled="!!downloadingReleaseId" :title="downloadingReleaseId===r.id?'正在生成发布包':'生成并下载发布包'" @click="pkg(r)"><LoaderCircle v-if="downloadingReleaseId===r.id" class="spin"/><Download v-else/>{{downloadingReleaseId===r.id?'生成中…':'下载'}}</button><button class="icon-btn" title="回滚" :disabled="r.id===project?.currentReleaseId" @click="rollback(r)"><Undo2/></button></td></tr></tbody></table><div v-if="!releases.length" class="empty">暂无成功版本</div></section>
<section v-if="tab==='runtime'" class="runtime-layout"><article class="runtime-control"><span :class="['big-indicator',runtime.state==='Running'?'on':'']"><i></i></span><span class="eyebrow">MANAGED PROCESS</span><h2>{{statusText(runtime.state)}}</h2><dl><dt>PID</dt><dd>{{runtime.processId||'—'}}</dd><dt>启动时间</dt><dd>{{when(runtime.startedAtUtc)}}</dd><dt>退出码</dt><dd>{{runtime.exitCode??'—'}}</dd></dl><div class="button-row"><button class="btn primary" :disabled="runtime.state==='Running'||busy" @click="runtimeAction('start')"><Play/>启动</button><button class="btn ghost" :disabled="runtime.state!=='Running'||busy" @click="runtimeAction('restart')"><RotateCw/>重启</button><button class="btn danger" :disabled="runtime.state!=='Running'||busy" @click="runtimeAction('stop')"><Square/>停止</button></div></article><article class="runtime-log"><div class="console-head"><span><TerminalSquare/>运行日志</span><button class="btn ghost" @click="load"><RotateCw/>刷新</button></div><pre class="console">{{runtimeLog||'尚无运行日志。'}}</pre></article></section>
<section v-if="tab==='config'" class="config-layout"><article class="json-editor"><div class="console-head"><span>appsettings.json</span><button class="btn ghost" @click="json=JSON.stringify(JSON.parse(json),null,2)">格式化</button></div><textarea v-model="json" spellcheck="false"></textarea><div class="editor-foot"><input v-model="comment" placeholder="本次修改说明（必填）"/><button v-if="session.user?.role==='Admin'" class="btn primary" :disabled="!comment||busy" @click="saveConfig"><Save/>保存新版本</button></div></article><aside class="config-history"><h3>配置版本</h3><div v-for="c in configs" :key="c.id" :class="['config-version',c.isActive?'active':'']"><span>v{{c.version}}</span><div><strong>{{c.comment||'无说明'}}</strong><small>{{when(c.createdAtUtc)}}</small></div><CheckCircle2 v-if="c.isActive"/><button v-else-if="session.user?.role==='Admin'" class="icon-btn" @click="activate(c)"><Undo2/></button></div></aside></section>
</template>
