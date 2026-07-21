<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { KeyRound, LoaderCircle, Plus, Users as UsersIcon, X } from 'lucide-vue-next'
import { api, post, put, when } from '../api'
import { PageHeader } from '../components/Common.vue'
import { session } from '../main'

const rows = ref<any[]>([])
const showCreate = ref(false)
const createForm = ref({ userName: '', password: '', role: 'Viewer', isEnabled: true })
const resetUser = ref<any>(null)
const resetForm = ref({ password: '', confirmation: '' })
const busy = ref(false)
const notice = ref<{ type:'success'|'error', message:string } | null>(null)

async function load() { rows.value = await api('/users') }

async function create() {
  busy.value = true
  notice.value = null
  try {
    await post('/users', createForm.value)
    notice.value = { type: 'success', message: `用户 ${createForm.value.userName} 已创建。` }
    showCreate.value = false
    createForm.value = { userName: '', password: '', role: 'Viewer', isEnabled: true }
    await load()
  } catch (e:any) {
    notice.value = { type: 'error', message: e.message }
  } finally { busy.value = false }
}

async function saveUser(u:any, enabled = u.isEnabled) {
  notice.value = null
  try {
    await put(`/users/${u.id}`, { role: u.role, isEnabled: enabled, password: null })
    notice.value = { type: 'success', message: `${u.userName} 的账户设置已更新。` }
    await load()
  } catch (e:any) {
    notice.value = { type: 'error', message: e.message }
    await load()
  }
}

function openPasswordReset(u:any) {
  resetUser.value = u
  resetForm.value = { password: '', confirmation: '' }
  notice.value = null
}

function closePasswordReset(force = false) {
  if (busy.value && !force) return
  resetUser.value = null
  resetForm.value = { password: '', confirmation: '' }
}

async function resetPassword() {
  if (!resetUser.value) return
  if (resetForm.value.password.length < 10) { notice.value = { type: 'error', message: '新密码至少需要 10 个字符。' }; return }
  if (resetForm.value.password !== resetForm.value.confirmation) { notice.value = { type: 'error', message: '两次输入的新密码不一致。' }; return }
  busy.value = true
  notice.value = null
  const userName = resetUser.value.userName
  try {
    await put(`/users/${resetUser.value.id}`, { role: resetUser.value.role, isEnabled: resetUser.value.isEnabled, password: resetForm.value.password })
    closePasswordReset(true)
    notice.value = { type: 'success', message: `${userName} 的密码已重置，现有登录已注销，下次登录必须修改密码。` }
    await load()
  } catch (e:any) {
    notice.value = { type: 'error', message: e.message }
  } finally { busy.value = false }
}

onMounted(load)
</script>

<template>
  <PageHeader eyebrow="ACCESS CONTROL" title="用户管理" description="分配角色、禁用账户和重置密码。">
    <button class="btn primary" @click="showCreate=!showCreate"><Plus/>新增用户</button>
  </PageHeader>

  <div v-if="notice" :class="['user-notice',notice.type]" role="status"><span>{{notice.message}}</span><button type="button" aria-label="关闭提示" @click="notice=null">×</button></div>

  <form v-if="showCreate" class="inline-create" @submit.prevent="create">
    <input v-model="createForm.userName" placeholder="用户名" required/>
    <input v-model="createForm.password" type="password" minlength="10" placeholder="初始密码（至少 10 位）" required/>
    <select v-model="createForm.role"><option>Admin</option><option>Operator</option><option>Viewer</option></select>
    <button class="btn primary" :disabled="busy">{{busy?'创建中…':'创建'}}</button>
  </form>

  <section class="table-card">
    <table>
      <thead><tr><th>用户</th><th>角色</th><th>状态</th><th>最后登录</th><th>下次登录改密</th><th>操作</th></tr></thead>
      <tbody><tr v-for="u in rows" :key="u.id">
        <td><strong>{{u.userName}}</strong><small v-if="u.id===session.user?.id" class="current-user-tag">当前账户</small></td>
        <td><select v-model="u.role" :disabled="u.id===session.user?.id" :title="u.id===session.user?.id?'不能修改当前登录账户的角色':''" @change="saveUser(u)"><option>Admin</option><option>Operator</option><option>Viewer</option></select></td>
        <td><span :class="['state-pill',u.isEnabled?'live':'']"><i></i>{{u.isEnabled?'启用':'禁用'}}</span></td>
        <td>{{when(u.lastLoginUtc)}}</td>
        <td>{{u.mustChangePassword?'是':'否'}}</td>
        <td><div class="user-actions"><button type="button" class="btn ghost" @click="openPasswordReset(u)"><KeyRound/>重置密码</button><button type="button" class="btn ghost" :disabled="u.id===session.user?.id" :title="u.id===session.user?.id?'不能禁用当前登录账户':''" @click="saveUser(u,!u.isEnabled)">{{u.isEnabled?'禁用':'启用'}}</button></div></td>
      </tr></tbody>
    </table>
    <div v-if="!rows.length" class="empty"><UsersIcon/>暂无用户</div>
  </section>

  <div v-if="resetUser" class="password-modal" @click.self="closePasswordReset()">
    <form class="password-card" @submit.prevent="resetPassword">
      <div class="password-card-head"><span><KeyRound/></span><button type="button" aria-label="关闭" @click="closePasswordReset()"><X/></button></div>
      <span class="eyebrow">CREDENTIAL RESET</span>
      <h2>重置 {{resetUser.userName}} 的密码</h2>
      <p>重置后将注销该用户的所有现有登录，并要求其下次登录时修改密码。</p>
      <label>新密码<input v-model="resetForm.password" type="password" minlength="10" autocomplete="new-password" autofocus placeholder="至少 10 个字符" required/></label>
      <label>确认新密码<input v-model="resetForm.confirmation" type="password" minlength="10" autocomplete="new-password" placeholder="再次输入新密码" required/></label>
      <div class="password-card-actions"><button type="button" class="btn ghost" :disabled="busy" @click="closePasswordReset()">取消</button><button class="btn primary" :disabled="busy"><LoaderCircle v-if="busy" class="spin"/><KeyRound v-else/>{{busy?'正在重置…':'确认重置'}}</button></div>
    </form>
  </div>
</template>
