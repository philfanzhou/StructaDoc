import { createRouter, createWebHistory, type RouteLocationNormalized } from 'vue-router'
import WorkspaceView from './views/WorkspaceView.vue'
import { message } from './messages'
import { ensureSession, safeReturnUrl } from './session'

declare module 'vue-router' {
  interface RouteMeta {
    requires?: 'user' | 'administrator'
    bare?: boolean
  }
}

// One Host serves the document workspace at `/` and the administration area at `/admin`.
// Administration is lazily imported so a workspace-only visitor never downloads it. The path
// is not an access boundary: this SPA is public static content and every administrative API
// enforces the administrator policy on the server.
const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'workspace', component: WorkspaceView, meta: { requires: 'user' } },
    { path: '/setup', name: 'setup', component: () => import('./views/SetupView.vue'), meta: { bare: true } },
    { path: '/signin', name: 'signin', component: () => import('./views/SignInView.vue'), meta: { bare: true } },
    { path: '/admin', name: 'administration', component: () => import('./views/AdministrationView.vue'), meta: { requires: 'administrator' } },
    { path: '/admin/signin', name: 'administrator-signin', component: () => import('./views/AdministratorSignInView.vue'), meta: { bare: true } },
    { path: '/:unmatched(.*)*', redirect: '/' },
  ],
})

router.beforeEach(async to => {
  try {
    const current = await ensureSession()
    // Until the first administrator exists there is nothing to sign in to, so every route leads to
    // setup. Once one exists, setup no longer exists either, and the server says so too.
    if (current.setupRequired) return to.name === 'setup' ? true : { name: 'setup' }
    if (to.name === 'setup') return { path: '/' }
    if (to.meta.requires === 'user' && !current.authenticated) return { name: 'signin', query: { returnUrl: to.fullPath } }
    if (to.meta.requires === 'administrator' && !current.isAdministrator) return { name: 'administrator-signin', query: { returnUrl: to.fullPath } }
    if (to.name === 'signin' && current.authenticated) return returnTarget(to, '/')
    if (to.name === 'administrator-signin' && current.isAdministrator) return returnTarget(to, '/admin')
    return true
  } catch (e) {
    message((e as Error).message, true)
    return false
  }
})

function returnTarget(to: RouteLocationNormalized, fallback: string) {
  return { path: safeReturnUrl(to.query.returnUrl, fallback) }
}

export default router
