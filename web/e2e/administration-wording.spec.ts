import { expect, test, type Route } from '@playwright/test'

async function json(route: Route, body: unknown) {
  await route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) })
}
const administrator = {
  authenticated: true, subjectType: 'administrator', subjectId: 'wording-admin',
  displayName: 'Test administrator', isAdministrator: true, oidcEnabled: false, setupRequired: false,
}

test('setup explains its existing password length and deployment settings', async ({ page }) => {
  await page.route('**/api/v1/session', route => json(route, { ...administrator, authenticated: false, isAdministrator: false, setupRequired: true }))
  await page.goto('/setup')
  await expect(page.getByText('首次创建表单要求密码至少 12 位', { exact: false })).toBeVisible()
  await expect(page.getByLabel('密码', { exact: true })).toHaveAttribute('minlength', '12')
  await expect(page.getByLabel('确认密码', { exact: true })).toHaveAttribute('minlength', '12')
  await expect(page.getByText('部分设置由部署方控制，网页修改的设置可能需要重启服务。', { exact: false })).toBeVisible()
})

test('administration explains OIDC, storage, and key rotation while preserving setting requests', async ({ page }) => {
  const values: Record<string, string> = {
    'Oidc:Enabled': 'false', 'Oidc:Authority': 'https://identity.example.test',
    'Oidc:ClientId': 'client', 'Oidc:ClientSecret': '', 'Oidc:RequireHttpsMetadata': 'true',
    'Oidc:NameClaim': 'name', 'Oidc:EmailClaim': 'email', 'Oidc:RoleClaim': 'role', 'Oidc:AdministratorRole': 'administrator',
    'Storage:Provider': 'S3', 'Storage:RootPath': '/data/storage', 'Storage:ServiceUrl': 'https://s3.example.test',
    'Storage:Region': 'test-region', 'Storage:Bucket': 'documents', 'Storage:Prefix': '',
    'Storage:AccessKey': '', 'Storage:SecretKey': '', 'Storage:ForcePathStyle': 'true',
    'Database:Provider': 'Sqlite', 'Database:ConnectionString': '', 'Database:ServerVersion': '',
  }
  let saved: unknown
  let rotated = false
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/v1/session') return json(route, administrator)
    if (path === '/api/v1/admin/setup-claim') return json(route, null)
    if (path === '/api/v1/admin/antiforgery') return json(route, { requestToken: 'test-token', headerName: 'X-CSRF-TOKEN' })
    if (path === '/api/v1/admin/settings') {
      if (route.request().method() === 'PUT') {
        saved = route.request().postDataJSON()
        const { key, value } = saved as { key: string; value: string }; values[key] = value
        return json(route, { restartRequired: true })
      }
      return json(route, Object.entries(values).map(([key, value]) => ({
        key, value, kind: key.endsWith('Enabled') || key.endsWith('Metadata') || key.endsWith('Style') ? 'Boolean' : 'Text',
        requiresRestart: true, isManagedExternally: false, isStored: true, isPendingRestart: false,
        minimum: 0, maximum: 0, allowedValues: key === 'Storage:Provider' ? ['Local', 'S3'] : key === 'Database:Provider' ? ['Sqlite'] : [],
      })))
    }
    if (path === '/api/v1/admin/settings/oidc') return json(route, { enabled: false, startupFault: null, callbackPath: '/signin-oidc', signedOutCallbackPath: '/signout-callback-oidc', scopes: ['openid', 'profile', 'email'] })
    if (path === '/api/v1/admin/settings/oidc/test') return json(route, { succeeded: false, code: 'IssuerMismatch', detail: '', issuer: null })
    if (path === '/api/v1/admin/settings/storage') return json(route, { provider: 'S3', startupFault: null, hasCredential: true })
    if (path === '/api/v1/admin/settings/database') return json(route, { provider: 'Sqlite', startupFault: null, isReachable: true, hasPendingMigrations: false })
    if (path === '/api/v1/admin/api-clients') return json(route, [{ id: 'test-client', name: 'Test integration', isActive: true, scopes: ['documents:read', 'parses:write'] }])
    if (path === '/api/v1/admin/api-clients/test-client/rotate') { rotated = true; return json(route, { credential: 'test-new-api-key' }) }
    if (path === '/api/v1/system/info') return json(route, { version: 'test' })
    return json(route, [])
  })
  await page.goto('/admin')
  await expect(page.getByText('上传后，点击“开始新解析”', { exact: false })).toBeVisible()
  await expect(page.getByText('自建服务的数据传输范围取决于服务地址与部署网络', { exact: false })).toBeVisible()
  await expect(page.getByText('Worker__Enabled 控制，默认已启用', { exact: false })).toBeVisible()
  const oidc = page.locator('section').filter({ has: page.getByRole('heading', { name: /组织账号登录/ }) })
  await oidc.locator('summary').click()
  await expect(oidc.getByText('管理员仍可用本地账号访问工作台和管理页', { exact: false })).toBeVisible()
  const authority = oidc.getByLabel('身份平台地址（issuer）', { exact: false })
  await authority.fill('https://new-identity.example.test'); await authority.press('Tab')
  await expect.poll(() => saved).toEqual({ key: 'Oidc:Authority', value: 'https://new-identity.example.test' })
  await expect(oidc.getByLabel('姓名字段（name claim）', { exact: true })).toHaveValue('name')
  await expect(oidc.getByLabel('客户端密钥', { exact: true })).toHaveValue('')
  await expect(oidc.getByLabel('客户端密钥', { exact: true })).toHaveAttribute('placeholder', '已保存，内容不再显示')
  await oidc.getByRole('button', { name: '测试连接' }).click()
  await expect(oidc.getByText('登录配置文档声明的平台地址（issuer）与填写的地址不同，登录会被拒绝')).toBeVisible()
  await expect(oidc.getByText('登录请求的权限范围（scope）', { exact: false })).toBeVisible()
  const storage = page.locator('section').filter({ has: page.getByRole('heading', { name: /文件存储/ }) })
  await storage.locator('summary').click()
  await expect(storage.getByRole('checkbox', { name: /在 S3 地址路径中包含存储桶名称/ })).toBeChecked()
  await expect(storage.getByLabel('访问密钥标识（Access Key）', { exact: true })).toHaveValue('')
  await expect(page.getByText('管理员账号与本页设置保存在另一个本地数据库中', { exact: false })).toBeVisible()
  await page.getByText('管理服务客户端', { exact: true }).click()
  await expect(page.getByText('查看文档（documents:read） · 开始和管理解析（parses:write）')).toBeVisible()
  await page.getByRole('button', { name: '更换 API 密钥' }).click()
  await expect.poll(() => rotated).toBe(true)
  await expect(page.locator('.credential code')).toHaveText('test-new-api-key')
  await expect(page.getByText('新 API 密钥只显示一次，请立即保存；旧密钥已失效', { exact: true })).toBeVisible()
})
