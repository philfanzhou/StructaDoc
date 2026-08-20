import { expect, test } from '@playwright/test'

test('layout blocks support keyboard selection and expose their state', async ({ page }) => {
  const documentId = '10000000-0000-0000-0000-000000000001'
  const runId = '20000000-0000-0000-0000-000000000001'
  const createdAt = '2026-08-20T00:00:00Z'
  const blocks = [
    {
      id: '30000000-0000-0000-0000-000000000001',
      sequence: 0,
      pageNumber: 1,
      type: 'title',
      content: 'Keyboard-accessible title',
      boundingBox: { x0: 0.1, y0: 0.1, x1: 0.9, y1: 0.2 },
    },
    {
      id: '30000000-0000-0000-0000-000000000002',
      sequence: 1,
      pageNumber: 1,
      type: 'text',
      content: 'Keyboard-accessible body',
      boundingBox: { x0: 0.1, y0: 0.25, x1: 0.9, y1: 0.5 },
    },
  ]

  await page.route('**/api/v1/**', async route => {
    const url = new URL(route.request().url())
    let body: unknown

    if (url.pathname === '/api/v1/session') {
      body = { authenticated: true, subjectType: 'user', subjectId: 'user-1', displayName: 'Test user', isAdministrator: false, oidcEnabled: true, setupRequired: false }
    } else if (url.pathname === '/api/v1/parse-execution') {
      body = { workerEnabled: true, providerCredentialMissing: false }
    } else if (url.pathname === '/api/v1/documents') {
      body = { items: [{ id: documentId, originalFileName: 'layout.pdf', mediaType: 'application/pdf', extension: '.pdf', sizeBytes: 1024, sha256: 'a'.repeat(64), createdAt, latestParseStatus: 'succeeded', ownedByCurrentUser: true }] }
    } else if (url.pathname === `/api/v1/documents/${documentId}/parse-runs`) {
      body = [{ id: runId, documentId, status: 'succeeded', providerType: 'test', attemptCount: 1, maxAttempts: 1, createdAt, completedAt: createdAt }]
    } else if (url.pathname === `/api/v1/parse-runs/${runId}/pages`) {
      body = [{ number: 1, width: 1000, height: 1400, unit: 'pt' }]
    } else if (url.pathname === `/api/v1/parse-runs/${runId}/blocks`) {
      body = { items: blocks }
    } else if (url.pathname === `/api/v1/parse-runs/${runId}/assets` || url.pathname === `/api/v1/parse-runs/${runId}/artifacts`) {
      body = []
    } else {
      await route.continue()
      return
    }

    await route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) })
  })

  await page.goto('/')
  await page.getByText('layout.pdf', { exact: true }).click()
  await page.locator('.run-open').click()
  await page.locator('.result-tabs').getByRole('button', { name: /版面/ }).click()

  const layout = page.getByRole('group', { name: '页面版面示意' })
  const title = layout.getByRole('button', { name: '第 1 块，类型 标题' })
  const body = layout.getByRole('button', { name: '第 2 块，类型 正文' })

  await expect(title).toHaveAttribute('tabindex', '0')
  await expect(title).toHaveAttribute('aria-pressed', 'false')
  await page.locator('.page-picker button').focus()
  await page.keyboard.press('Tab')
  await expect(title).toBeFocused()
  await expect(title).toHaveCSS('outline-style', 'solid')
  await title.press('Enter')
  await expect(title).toHaveAttribute('aria-pressed', 'true')
  await expect(page.locator('.layout-selected')).toContainText('Keyboard-accessible title')

  await body.focus()
  await body.press('Space')
  await expect(body).toHaveAttribute('aria-pressed', 'true')
  await expect(title).toHaveAttribute('aria-pressed', 'false')
  await expect(page.locator('.layout-selected')).toContainText('Keyboard-accessible body')
})
