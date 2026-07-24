import { session, router } from './main'

export async function api<T = any>(path: string, options: RequestInit = {}): Promise<T> {
  const headers = new Headers(options.headers)
  if (session.token) headers.set('Authorization', `Bearer ${session.token}`)
  if (options.body && !(options.body instanceof FormData)) headers.set('Content-Type', 'application/json')
  const response = await fetch(`/api${path}`, { ...options, headers })
  if (response.status === 401) { session.token = ''; session.user = null; localStorage.clear(); router.push('/login'); throw new Error('登录已过期') }
  if (response.status === 403) throw new Error(`权限不足：当前账户没有执行此操作的权限，请使用 Admin 或 Operator 角色。`)
  if (!response.ok) { const body = await response.json().catch(() => ({})); throw new Error(body.message || `请求失败 (${response.status})`) }
  if (response.status === 204) return undefined as T
  return response.json()
}
export const post = <T=any>(path:string, body:any={}) => api<T>(path, { method:'POST', body:JSON.stringify(body) })
export const put = <T=any>(path:string, body:any) => api<T>(path, { method:'PUT', body:JSON.stringify(body) })
export function utcDate(value:string|undefined|null) {
  if (!value) return null
  let normalized = value.trim().replace(/^(\d{4}-\d{2}-\d{2})\s/, '$1T').replace(/(\.\d{3})\d+/, '$1')
  if (!/(?:Z|[+-]\d{2}:?\d{2})$/i.test(normalized)) normalized += 'Z'
  const result = new Date(normalized)
  return Number.isNaN(result.getTime()) ? null : result
}
export function when(value:string|undefined|null) { return utcDate(value)?.toLocaleString('zh-CN', { hour12:false }) || '—' }
export function timeOnly(value:string|undefined|null) { return utcDate(value)?.toLocaleTimeString('zh-CN', { hour12:false }) || '—' }
export function localizeRuntimeLog(content:string) {
  return content.replace(/^(\d{4}-\d{2}-\d{2}T\S+)(?=\s+\[(?:OUT|ERR)\])/gm, value => when(value))
}
export function statusText(value:string) { return ({Queued:'排队中',FetchingSource:'获取源码',BuildingBackend:'构建后台',BuildingAdmin:'构建管理端',BuildingTerminal:'构建终端',Assembling:'组装产物',ApplyingConfiguration:'应用配置',Validating:'校验',Switching:'切换版本',Starting:'启动中',Succeeded:'成功',Failed:'失败',Cancelled:'已取消',RolledBack:'已回滚',Interrupted:'已中断',Running:'运行中',Stopped:'已停止',NotRunning:'未运行',Crashed:'异常退出'} as any)[value] || value }
