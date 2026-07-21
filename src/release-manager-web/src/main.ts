import { createApp, reactive } from 'vue'
import { createRouter, createWebHistory } from 'vue-router'
import App from './App.vue'
import Login from './views/Login.vue'
import Dashboard from './views/Dashboard.vue'
import ProjectEditor from './views/ProjectEditor.vue'
import ProjectWorkspace from './views/ProjectWorkspace.vue'
import Users from './views/Users.vue'
import Audit from './views/Audit.vue'
import Settings from './views/Settings.vue'
import './styles.css'
import './log-console.css'
import './download-feedback.css'
import './workspace-cleanup.css'
import './user-management.css'

export const session = reactive<{token:string; user:any}>({ token: localStorage.getItem('token') || '', user: JSON.parse(localStorage.getItem('user') || 'null') })
export const router = createRouter({ history: createWebHistory(), routes: [
  { path: '/login', component: Login, meta: { public: true } },
  { path: '/', component: Dashboard },
  { path: '/projects/new', component: ProjectEditor },
  { path: '/projects/:id/edit', component: ProjectEditor },
  { path: '/projects/:id/:tab?', component: ProjectWorkspace },
  { path: '/users', component: Users, meta: { admin: true } },
  { path: '/audit', component: Audit, meta: { admin: true } },
  { path: '/settings', component: Settings, meta: { admin: true } }
]})
router.beforeEach(to => { if (!to.meta.public && !session.token) return '/login'; if (to.meta.admin && session.user?.role !== 'Admin') return '/' })
createApp(App).use(router).mount('#app')
