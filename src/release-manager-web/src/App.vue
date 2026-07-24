<script setup lang="ts">
import { computed, ref } from 'vue'
import { RouterView, useRoute } from 'vue-router'
import { LayoutDashboard, FolderKanban, Users, ScrollText, Settings, LogOut, Boxes, KeyRound, LoaderCircle, X } from 'lucide-vue-next'
import { session, router } from './main'
import { api } from './api'
const route = useRoute(); const isLogin = computed(() => route.path === '/login')
const showPassword = ref(false), changingPassword = ref(false), passwordError = ref('')
const passwordForm = ref({ currentPassword: '', newPassword: '', confirmation: '' })
const nav = computed(() => [
  {to:'/',label:'项目总览',icon:LayoutDashboard}, {to:'/projects/new',label:'新建项目',icon:FolderKanban},
  ...(session.user?.role === 'Admin' ? [{to:'/users',label:'用户管理',icon:Users},{to:'/audit',label:'审计日志',icon:ScrollText},{to:'/settings',label:'系统设置',icon:Settings}] : [])
])
async function logout(){ try{ await api('/auth/logout',{method:'POST'}) }finally{ session.token='';session.user=null;localStorage.clear();router.push('/login') } }
function closePassword() { if (!changingPassword.value && !session.user?.mustChangePassword) { showPassword.value=false; passwordError.value=''; passwordForm.value={currentPassword:'',newPassword:'',confirmation:''} } }
async function changePassword() {
  passwordError.value = ''
  if (passwordForm.value.newPassword.length < 10) { passwordError.value='新密码至少需要 10 个字符。'; return }
  if (passwordForm.value.newPassword !== passwordForm.value.confirmation) { passwordError.value='两次输入的新密码不一致。'; return }
  changingPassword.value = true
  try {
    await api('/auth/password',{method:'PUT',body:JSON.stringify({currentPassword:passwordForm.value.currentPassword,newPassword:passwordForm.value.newPassword})})
    session.user = {...session.user,mustChangePassword:false}
    localStorage.setItem('user',JSON.stringify(session.user))
    showPassword.value=false
    passwordForm.value={currentPassword:'',newPassword:'',confirmation:''}
  } catch(e:any) { passwordError.value=e.message }
  finally { changingPassword.value=false }
}
</script>
<template>
  <RouterView v-if="isLogin" />
  <div v-else class="shell">
    <aside class="rail">
      <div class="brand"><span class="brand-mark"><Boxes :size="22"/></span><div><strong>铸剑台</strong><small>RELEASE FOUNDRY</small></div></div>
      <nav><RouterLink v-for="item in nav" :key="item.to" :to="item.to"><component :is="item.icon" :size="18"/><span>{{item.label}}</span></RouterLink></nav>
      <div class="account"><div class="avatar">{{session.user?.userName?.slice(0,1).toUpperCase()}}</div><div><strong>{{session.user?.userName}}</strong><small>{{session.user?.role}}</small></div><div class="account-tools"><button class="icon-btn" title="修改密码" @click="showPassword=true"><KeyRound :size="16"/></button><button class="icon-btn" title="退出" @click="logout"><LogOut :size="17"/></button></div></div>
    </aside>
    <main class="canvas"><RouterView /></main>
  </div>
  <div v-if="!isLogin&&(showPassword||session.user?.mustChangePassword)" class="password-modal" @click.self="closePassword">
    <form class="password-card" @submit.prevent="changePassword">
      <div class="password-card-head"><span><KeyRound/></span><button v-if="!session.user?.mustChangePassword" type="button" aria-label="关闭" @click="closePassword"><X/></button></div>
      <span class="eyebrow">ACCOUNT SECURITY</span>
      <h2>{{session.user?.mustChangePassword?'首次登录，请修改密码':'修改登录密码'}}</h2>
      <p>{{session.user?.mustChangePassword?'管理员已设置临时密码。修改完成后才能继续安全使用平台。':'修改成功后，其他设备上的登录会自动失效。'}}</p>
      <label>当前密码<input v-model="passwordForm.currentPassword" type="password" autocomplete="current-password" required autofocus/></label>
      <label>新密码<input v-model="passwordForm.newPassword" type="password" minlength="10" autocomplete="new-password" placeholder="至少 10 个字符" required/></label>
      <label>确认新密码<input v-model="passwordForm.confirmation" type="password" minlength="10" autocomplete="new-password" required/></label>
      <div v-if="passwordError" class="alert error password-error">{{passwordError}}</div>
      <div class="password-card-actions"><button v-if="!session.user?.mustChangePassword" type="button" class="btn ghost" :disabled="changingPassword" @click="closePassword">取消</button><button class="btn primary" :disabled="changingPassword"><LoaderCircle v-if="changingPassword" class="spin"/><KeyRound v-else/>{{changingPassword?'正在修改…':'确认修改'}}</button></div>
    </form>
  </div>
</template>
