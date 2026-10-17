import { expect, test, type Page, type Route } from '@playwright/test'

const administratorUsername = process.env.STRUCTADOC_E2E_ADMIN_USERNAME ?? 'structadoc-admin'
const administratorPassword = process.env.STRUCTADOC_E2E_ADMIN_PASSWORD ?? 'StructaDoc-E2E-Password'

type ReportedSetting = {
  key: string
  isManagedExternally: boolean
  isStored: boolean
  isPendingRestart: boolean
}

// The page resolves one status per key in a fixed priority, so the expectation resolves the same
// way: a pinned key is never stored, and a stored value the running process has not picked up is
// waiting for a restart rather than merely customized.
function statusOf(setting: ReportedSetting) {
  if (setting.isManagedExternally) return 'pinned'
  if (setting.isPendingRestart) return 'pending'
  return setting.isStored ? 'customized' : 'default'
}

const areas: Record<string, string[]> = {
  worker: ['Worker:'],
  documents: ['Documents:'],
  'provider-results': ['ProviderResults:'],
  normalization: ['ProviderResultNormalization:'],
  libreoffice: ['LibreOffice:'],
  logging: ['Logging:'],
  oidc: ['Oidc:'],
  storage: ['Storage:'],
  database: ['Database:'],
  authentication: ['Authentication:'],
}

// The three domains with panels of their own render their keys there rather than as rows among the
// service settings, so the grouped rows owe exactly the rest of the catalog.
const ownPanelPrefixes = ['Oidc:', 'Storage:', 'Database:']

async function signInAsAdministrator(page: Page) {
  await page.goto('/admin')
  await expect(page).toHaveURL(/\/admin\/signin/)
  await page.locator('form input[name="username"]').fill(administratorUsername)
  await page.locator('form input[type="password"]').fill(administratorPassword)
  await page.locator('form button').click()
  // The sign-in URL keeps the requested path as a query value, so a URL pattern can match a page
  // that is still the form; the page's own heading is what says the sign-in actually landed.
  await expect(page.getByText('ADMINISTRATION', { exact: true })).toBeVisible()
}

test('administration groups every catalog key by domain and keeps overview counts on the API', async ({ page }) => {
  await signInAsAdministrator(page)

  // What the page owes is decided by what the service publishes, not by a copy of the catalog in
  // this test: the response the page reads is itself the expectation the page is held to.
  const settings = await page.evaluate(async () =>
    (await (await fetch('/api/v1/admin/settings')).json()) as ReportedSetting[])
  expect(settings.length).toBeGreaterThan(0)

  const overview = page.locator('[data-settings-overview]')
  await expect(overview).toBeVisible()

  for (const [areaId, prefixes] of Object.entries(areas)) {
    const keys = settings.filter(setting => prefixes.some(prefix => setting.key.startsWith(prefix)))
    // A section the service publishes keys for but the page groups under no area would mean the
    // grouping and the catalog disagree; both are derived from the same prefixes.
    expect(keys.length, `catalog keys for the ${areaId} area`).toBeGreaterThan(0)
    const statuses = keys.map(statusOf)
    const pending = statuses.filter(status => status === 'pending').length
    const pinned = statuses.filter(status => status === 'pinned').length
    const customized = statuses.filter(status => status === 'customized').length
    const row = overview.locator(`[data-overview-area="${areaId}"]`)
    await expect(row).toBeVisible()
    await expect(row).toContainText(`${keys.length} 项设置`)
    if (pending) await expect(row).toContainText(`${pending} 项待重启`)
    if (pinned) await expect(row).toContainText(`${pinned} 项由部署管理`)
    if (customized) await expect(row).toContainText(`${customized} 项已调整`)
    if (!pending && !pinned && !customized) await expect(row.getByText('全部默认', { exact: true })).toBeVisible()
  }

  const panelKeys = settings
    .filter(setting => !ownPanelPrefixes.some(prefix => setting.key.startsWith(prefix)))
    .map(setting => setting.key)
  const rows = page.locator('.setting-group .admin-list > div')
  await expect(rows).toHaveCount(panelKeys.length)
  const renderedKeys = await rows.locator('small').allTextContents()
  expect(renderedKeys.slice().sort()).toEqual(panelKeys.slice().sort())
  // A key rendered under its own spelling means the label map lost it, and an administrator would
  // be reading configuration names instead of descriptions.
  const renderedLabels = await rows.locator('strong').allTextContents()
  for (const [index, key] of renderedKeys.entries()) {
    expect(renderedLabels[index], `label for ${key}`).not.toEqual(key)
  }
})

test('a setting change carries through the row badge, the overview, and the restart banner', async ({ page }) => {
  await signInAsAdministrator(page)

  const overview = page.locator('[data-settings-overview]')
  const group = page.locator('[data-setting-group="libreoffice"]')
  // A click on an open disclosure closes it, and a retry can find this group held open by the
  // pending restart a failed attempt left behind, so it is opened only when it is not already.
  if (!await group.evaluate(details => (details as HTMLDetailsElement).open)) {
    await group.locator('summary').click()
  }
  const enabledRow = group.locator('.admin-list > div').filter({ hasText: 'LibreOffice:Enabled' })
  const badge = enabledRow.locator('[data-setting-status]')
  // A retry of this test runs against the deployment a failed attempt already wrote to, so the row
  // is brought back to its default before anything is asserted about defaults.
  if ((await badge.textContent()) !== '默认') {
    await enabledRow.getByRole('button', { name: '恢复默认' }).click()
  }
  await expect(badge).toHaveText('默认')
  await expect(overview.locator('[data-overview-area="libreoffice"]')).toContainText('全部默认')

  // A stored value the running process has not picked up is what the service reports as pending a
  // restart; the page repeats that rather than inferring it from the key.
  await enabledRow.getByRole('button', { name: '关闭' }).click()
  await expect(badge).toHaveText('待重启生效')
  await expect(overview.locator('[data-overview-area="libreoffice"]')).toContainText('1 项待重启')
  await expect(page.getByText('部分设置需重启服务后才会生效', { exact: false })).toBeVisible()

  await enabledRow.getByRole('button', { name: '恢复默认' }).click()
  await expect(badge).toHaveText('默认')
  await expect(overview.locator('[data-overview-area="libreoffice"]')).toContainText('全部默认')
})

const administrator = {
  authenticated: true, subjectType: 'administrator', subjectId: 'settings-admin',
  displayName: 'Test administrator', isAdministrator: true, oidcEnabled: false, setupRequired: false,
}

function json(route: Route, body: unknown) {
  return route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) })
}

function settingRow(key: string, overrides: Partial<{
  value: string
  kind: string
  isManagedExternally: boolean
  isStored: boolean
  isPendingRestart: boolean
}> = {}) {
  return {
    key, value: '', kind: 'Integer', requiresRestart: true, isManagedExternally: false,
    isStored: false, isPendingRestart: false, minimum: 0, maximum: 0, allowedValues: [],
    ...overrides,
  }
}

test('grouped rows honor pinned and pending reports, keep unknown keys, and survive a failed read', async ({ page }) => {
  let settingsReachable = false
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/v1/session') return json(route, administrator)
    if (path === '/api/v1/admin/settings') {
      if (!settingsReachable) return route.fulfill({ status: 503, contentType: 'application/json', body: '{"title":"settings unavailable"}' })
      return json(route, [
        settingRow('Worker:MaxConcurrency', { value: '4', isManagedExternally: true }),
        settingRow('Worker:LeaseDuration', { value: '00:05:00', isStored: true, isPendingRestart: true }),
        settingRow('Future:SomethingElse', { value: 'on' }),
      ])
    }
    if (path === '/api/v1/admin/settings/oidc') return json(route, { enabled: false, startupFault: null, callbackPath: '/signin-oidc', signedOutCallbackPath: '/signout-callback-oidc', scopes: ['openid'] })
    if (path === '/api/v1/admin/settings/storage') return json(route, { provider: 'Local', startupFault: null, hasCredential: false })
    if (path === '/api/v1/admin/settings/database') return json(route, { provider: 'Sqlite', startupFault: null, isReachable: true, hasPendingMigrations: false })
    if (path === '/api/v1/admin/administrators') return json(route, [{ id: 'admin-1', username: 'admin', displayName: 'Admin', isActive: true, isCurrent: true }])
    if (path === '/api/v1/system/info') return json(route, { version: 'test' })
    return json(route, [])
  })

  // The settings read is one panel's source, not the page's: when it fails, the panels that live
  // in the business database — including the ones this page exists to repair a deployment with —
  // must still load, and the failure must be said rather than shown as an empty page.
  await page.goto('/admin')
  await expect(page.getByText('PROVIDERS', { exact: true })).toBeVisible()
  await expect(page.getByText('ADMINISTRATORS', { exact: true })).toBeVisible()
  await expect(page.locator('.toast.error')).toBeVisible()

  settingsReachable = true
  await page.reload()
  const overview = page.locator('[data-settings-overview]')
  const workerRow = overview.locator('[data-overview-area="worker"]')
  await expect(workerRow).toContainText('1 项由部署管理')
  await expect(workerRow).toContainText('1 项待重启')

  // A deployment-pinned key is read-only here, and says so, because the environment variable wins.
  const worker = page.locator('[data-setting-group="worker"]')
  const pinnedRow = worker.locator('.admin-list > div').filter({ hasText: 'Worker:MaxConcurrency' })
  await expect(pinnedRow.locator('[data-setting-status]')).toHaveText('由部署管理')
  await expect(pinnedRow.locator('input.setting-input')).toBeDisabled()
  await expect(pinnedRow.getByRole('button')).toHaveCount(0)

  const pendingRow = worker.locator('.admin-list > div').filter({ hasText: 'Worker:LeaseDuration' })
  await expect(pendingRow.locator('[data-setting-status]')).toHaveText('待重启生效')

  // A key the page's grouping does not know still renders — under a residual group rather than
  // disappearing — and a group with a pending restart does not start folded away. The residual
  // group holds nothing waiting on anyone, so presence rather than visibility is what it owes.
  await expect(page.locator('[data-setting-group="other"] .admin-list > div').filter({ hasText: 'Future:SomethingElse' })).toHaveCount(1)
  await expect(worker).toHaveAttribute('open', '')
})
