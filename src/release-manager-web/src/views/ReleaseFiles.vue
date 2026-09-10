<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { ArrowLeft, ChevronRight, Download, File, FilePlus2, Folder, FolderOpen, LoaderCircle, RefreshCw, Replace, ShieldAlert } from 'lucide-vue-next'
import { api, when } from '../api'
import { router, session } from '../main'
import { PageHeader } from '../components/Common.vue'

const route = useRoute()
const projectId = computed(() => String(route.params.projectId))
const releaseId = computed(() => String(route.params.releaseId))
const currentPath = ref(String(route.query.path || ''))
const result = ref<any>({ release: null, path: '', entries: [] })
const loading = ref(true), busy = ref(false), error = ref(''), notice = ref('')
const addInput = ref<HTMLInputElement>(), replaceInput = ref<HTMLInputElement>(), replaceTarget = ref<any>()
const canMaintain = computed(() => session.user?.role === 'Admin' || session.user?.role === 'Operator')
const breadcrumbs = computed(() => {
  const parts = currentPath.value.split('/').filter(Boolean)
  return [{ name: '版本根目录', path: '' }, ...parts.map((name, index) => ({ name, path: parts.slice(0, index + 1).join('/') }))]
})

async function load() {
  loading.value = true
  error.value = ''
  try {
    const query = currentPath.value ? `?path=${encodeURIComponent(currentPath.value)}` : ''
    result.value = await api(`/releases/${releaseId.value}/files${query}`)
    currentPath.value = result.value.path || ''
  } catch (e:any) { error.value = e.message }
  finally { loading.value = false }
}
async function openDirectory(path:string) {
  currentPath.value = path
  await router.replace({ query: path ? { path } : {} })
}
function selectNewFile() { addInput.value?.click() }
function selectReplacement(entry:any) { replaceTarget.value = entry; replaceInput.value?.click() }
async function upload(event:Event, replace:boolean) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) return
  if (replace && !confirm(`确认用「${file.name}」替换版本中的「${replaceTarget.value?.path}」？\n\n此操作会修改历史版本文件并使旧的 ZIP 发布包失效。`)) return
  if (!replace && !confirm(`确认向「/${currentPath.value}」新增文件「${file.name}」？`)) return
  busy.value = true; notice.value = ''; error.value = ''
  try {
    const form = new FormData()
    form.append('file', file)
    if (replace) form.append('path', replaceTarget.value.path)
    else form.append('directory', currentPath.value)
    await api(`/releases/${releaseId.value}/files${replace ? '/replace' : ''}`, { method: 'POST', body: form })
    notice.value = replace ? `已替换 ${replaceTarget.value.path}` : `已新增 ${file.name}`
    await load()
  } catch (e:any) { error.value = e.message }
  finally { busy.value = false; replaceTarget.value = null }
}
async function download(entry:any) {
  busy.value = true; notice.value = `正在下载 ${entry.name}…`; error.value = ''
  try {
    const response = await fetch(`/api/releases/${releaseId.value}/files/download?path=${encodeURIComponent(entry.path)}`, { headers: { Authorization: `Bearer ${session.token}` } })
    if (!response.ok) {
      const body = await response.json().catch(() => ({}))
      throw new Error(body.message || `下载失败（HTTP ${response.status}）`)
    }
    const blob = await response.blob()
    const url = URL.createObjectURL(blob)
    const anchor = document.createElement('a')
    anchor.href = url; anchor.download = entry.name; document.body.appendChild(anchor); anchor.click(); anchor.remove()
    window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
    notice.value = `${entry.name} 已开始下载。`
  } catch (e:any) { error.value = e.message; notice.value = '' }
  finally { busy.value = false }
}
function formatBytes(value:number|null) {
  if (value == null) return '—'
  const units = ['B','KB','MB','GB','TB']; let size = value, index = 0
  while (size >= 1024 && index < units.length - 1) { size /= 1024; index++ }
  return `${size.toFixed(index ? 1 : 0)} ${units[index]}`
}

watch(() => route.query.path, () => { currentPath.value = String(route.query.path || ''); void load() })
onMounted(load)
</script>

<template>
  <PageHeader eyebrow="RELEASE ARTIFACT EXPLORER" :title="result.release?.version||'版本文件'" :description="`SVN r${result.release?.svnRevision||'—'} · 构建于 ${when(result.release?.builtAtUtc)}`">
    <button class="btn ghost" @click="router.push(`/projects/${projectId}/versions`)"><ArrowLeft/>返回版本历史</button>
    <button class="btn ghost" :disabled="loading" @click="load"><RefreshCw/>刷新</button>
    <button v-if="canMaintain" class="btn primary" :disabled="busy" @click="selectNewFile"><FilePlus2/>新增文件</button>
  </PageHeader>

  <div class="artifact-warning"><ShieldAlert/><span><strong>版本维护区</strong> 修改会直接作用于此历史版本；若它正在运行，建议修改后重启项目。已有 ZIP 包将自动失效。</span></div>
  <div v-if="error" class="alert error">{{error}}</div>
  <div v-if="notice" class="artifact-notice">{{notice}}</div>

  <section class="artifact-browser">
    <header class="artifact-toolbar">
      <nav aria-label="文件路径">
        <template v-for="(crumb,index) in breadcrumbs" :key="crumb.path">
          <ChevronRight v-if="index"/>
          <button :class="{active:index===breadcrumbs.length-1}" @click="openDirectory(crumb.path)">{{crumb.name}}</button>
        </template>
      </nav>
      <span>{{result.entries.length}} 项</span>
    </header>

    <div v-if="loading" class="artifact-empty"><LoaderCircle class="spin"/>正在读取版本目录…</div>
    <table v-else class="artifact-table">
      <thead><tr><th>名称</th><th>类型</th><th>大小</th><th>修改时间</th><th>操作</th></tr></thead>
      <tbody>
        <tr v-for="entry in result.entries" :key="entry.path">
          <td>
            <button v-if="entry.isDirectory" class="artifact-name folder" @click="openDirectory(entry.path)"><Folder/>{{entry.name}}</button>
            <span v-else class="artifact-name"><File/>{{entry.name}}</span>
          </td>
          <td>{{entry.isDirectory?'文件夹':'文件'}}</td><td class="mono">{{formatBytes(entry.size)}}</td><td>{{when(entry.lastModifiedUtc)}}</td>
          <td class="artifact-actions">
            <button v-if="!entry.isDirectory" title="下载文件" :disabled="busy" @click="download(entry)"><Download/>下载</button>
            <button v-if="!entry.isDirectory&&canMaintain" title="使用本地文件替换" :disabled="busy" @click="selectReplacement(entry)"><Replace/>替换</button>
            <button v-if="entry.isDirectory" @click="openDirectory(entry.path)"><FolderOpen/>打开</button>
          </td>
        </tr>
      </tbody>
    </table>
    <div v-if="!loading&&!result.entries.length" class="artifact-empty"><FolderOpen/>当前目录为空</div>
  </section>
  <input ref="addInput" class="visually-hidden" type="file" @change="upload($event,false)"/>
  <input ref="replaceInput" class="visually-hidden" type="file" @change="upload($event,true)"/>
</template>
