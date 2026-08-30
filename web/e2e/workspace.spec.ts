import { expect, test, type Page, type Route } from '@playwright/test'

const administratorUsername = process.env.STRUCTADOC_E2E_ADMIN_USERNAME ?? 'structadoc-admin'
const administratorPassword = process.env.STRUCTADOC_E2E_ADMIN_PASSWORD ?? 'StructaDoc-E2E-Password'

// The Host answers an unmatched `/api` path with 404, so a Provider pointed at one is a service that
// is reachable and refuses the submission. That is deliberate: it needs no second container, and a
// 404 is a permanent failure, so the run reaches a final status immediately instead of retrying for
// a minute. What it proves is the part that is image-specific — that the resident Worker in the
// published image claims a queued run, leases it, calls the Provider over HTTP, and records a final
// status. The success branch is covered against a real socket by ParseExecutionEndToEndTests.
const refusingProviderBaseUrl = 'http://127.0.0.1:8080/api/v1/system'

function deferred() {
  let resolve!: () => void
  const promise = new Promise<void>(done => { resolve = done })
  return { promise, resolve }
}

async function fulfillJson(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function settleUi(page: Page) {
  await page.evaluate(() => new Promise<void>(resolve => {
    requestAnimationFrame(() => requestAnimationFrame(() => resolve()))
  }))
}

function documentItem(id: string, originalFileName: string, latestParseStatus = 'succeeded') {
  return {
    id, originalFileName, latestParseStatus,
    mediaType: 'application/pdf', extension: '.pdf', sizeBytes: 1024,
    sha256: '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef',
    createdAt: '2026-08-29T00:00:00Z', ownedByCurrentUser: true,
  }
}

function parseRun(id: string, documentId: string, providerType: string) {
  return {
    id, documentId, providerType, status: 'succeeded', attemptCount: 1, maxAttempts: 3,
    createdAt: '2026-08-29T00:00:00Z', completedAt: '2026-08-29T00:00:01Z',
  }
}

const userSession = {
  authenticated: true, subjectType: 'user', subjectId: 'browser-test',
  issuer: 'https://identity.example.test', displayName: 'Browser test user',
  isAdministrator: false, oidcEnabled: true, setupRequired: false,
}

test('document selection ignores delayed success and failure from stale requests', async ({ page }) => {
  const documents = [
    documentItem('document-a', 'selection-a.pdf'),
    documentItem('document-b', 'selection-b.pdf'),
    documentItem('document-c', 'selection-c.pdf'),
  ]
  const aStarted = deferred(); const releaseA = deferred(); const aFinished = deferred()
  const cStarted = deferred(); const releaseC = deferred(); const cFinished = deferred()

  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/v1/session') return fulfillJson(route, userSession)
    if (path === '/api/v1/parse-execution') {
      return fulfillJson(route, { workerEnabled: true, providerCredentialMissing: false })
    }
    if (path === '/api/v1/documents') return fulfillJson(route, { items: documents })
    if (path === '/api/v1/documents/document-a/parse-runs') {
      aStarted.resolve(); await releaseA.promise
      await fulfillJson(route, [parseRun('run-a', 'document-a', 'Delayed provider A')])
      aFinished.resolve(); return
    }
    if (path === '/api/v1/documents/document-b/parse-runs') {
      return fulfillJson(route, [parseRun('run-b', 'document-b', 'Current provider B')])
    }
    if (path === '/api/v1/documents/document-c/parse-runs') {
      cStarted.resolve(); await releaseC.promise
      await fulfillJson(route, { title: 'Delayed stale failure' }, 503)
      cFinished.resolve(); return
    }
    return fulfillJson(route, { title: 'Unexpected mock request' }, 404)
  })

  await page.goto('/')
  await expect(page.getByText('selection-a.pdf', { exact: true })).toBeVisible()

  await page.getByText('selection-a.pdf', { exact: true }).click()
  await aStarted.promise
  await page.getByText('selection-b.pdf', { exact: true }).click()
  await expect(page.getByText('Current provider B', { exact: true })).toBeVisible()
  releaseA.resolve(); await aFinished.promise; await settleUi(page)
  await expect(page.getByText('Current provider B', { exact: true })).toBeVisible()
  await expect(page.getByText('Delayed provider A', { exact: true })).toHaveCount(0)

  await page.getByText('selection-c.pdf', { exact: true }).click()
  await cStarted.promise
  await expect(page.locator('.run-list .run-row')).toHaveCount(0)
  await page.getByText('selection-b.pdf', { exact: true }).click()
  await expect(page.getByText('Current provider B', { exact: true })).toBeVisible()
  releaseC.resolve(); await cFinished.promise; await settleUi(page)
  await expect(page.getByText('Current provider B', { exact: true })).toBeVisible()
  await expect(page.getByText('Delayed stale failure', { exact: true })).toHaveCount(0)
  await expect(page.locator('.toast.error')).toBeHidden()
})

test('workspace polling is single-flight, retries, and stops after unmount', async ({ page }) => {
  const document = documentItem('poll-document', 'polling.pdf', 'running')
  const pollStarted = deferred(); const releasePoll = deferred(); const pollFinished = deferred()
  const retryStarted = deferred()
  let runRequests = 0
  let activeRunRequests = 0
  let maximumActiveRunRequests = 0

  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/v1/session') return fulfillJson(route, userSession)
    if (path === '/api/v1/parse-execution') {
      return fulfillJson(route, { workerEnabled: true, providerCredentialMissing: false })
    }
    if (path === '/api/v1/documents') return fulfillJson(route, { items: [document] })
    if (path === '/api/v1/documents/poll-document/parse-runs') {
      runRequests += 1; activeRunRequests += 1
      maximumActiveRunRequests = Math.max(maximumActiveRunRequests, activeRunRequests)
      if (runRequests === 2) {
        pollStarted.resolve(); await releasePoll.promise
        await fulfillJson(route, { title: 'Transient polling failure' }, 503)
        activeRunRequests -= 1; pollFinished.resolve(); return
      }
      await fulfillJson(route, [{ ...parseRun('poll-run', 'poll-document', 'Polling provider'), status: 'running', completedAt: undefined }])
      activeRunRequests -= 1
      if (runRequests === 3) retryStarted.resolve()
      return
    }
    return fulfillJson(route, { title: 'Unexpected mock request' }, 404)
  })

  await page.goto('/')
  await page.getByText('polling.pdf', { exact: true }).click()
  await expect(page.getByText('Polling provider', { exact: true })).toBeVisible()

  await pollStarted.promise
  await page.waitForTimeout(3500)
  expect(runRequests).toBe(2)
  expect(maximumActiveRunRequests).toBe(1)

  releasePoll.resolve(); await pollFinished.promise; await retryStarted.promise
  expect(runRequests).toBe(3)
  expect(maximumActiveRunRequests).toBe(1)

  await page.goto('/admin')
  await expect(page).toHaveURL(/\/admin\/signin/)
  const requestsAfterUnmount = runRequests
  await page.waitForTimeout(3500)
  expect(runRequests).toBe(requestsAfterUnmount)
})

test('workspace polling refreshes final document state without a selection and stops', async ({ page }) => {
  const retryFinished = deferred(); const finalRefreshFinished = deferred()
  let documentRequests = 0
  let runRequests = 0

  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/v1/session') return fulfillJson(route, userSession)
    if (path === '/api/v1/parse-execution') {
      return fulfillJson(route, { workerEnabled: true, providerCredentialMissing: false })
    }
    if (path === '/api/v1/documents') {
      documentRequests += 1
      if (documentRequests === 2) {
        await fulfillJson(route, { title: 'Transient document polling failure' }, 503)
        retryFinished.resolve(); return
      }

      const status = documentRequests >= 3 ? 'succeeded' : 'running'
      await fulfillJson(route, { items: [documentItem('unselected-document', 'unselected.pdf', status)] })
      if (documentRequests === 3) finalRefreshFinished.resolve()
      return
    }
    if (path.endsWith('/parse-runs')) {
      runRequests += 1
      return fulfillJson(route, { title: 'Run list must not be requested without a selection' }, 500)
    }
    return fulfillJson(route, { title: 'Unexpected mock request' }, 404)
  })

  await page.goto('/')
  const documentStatus = page.locator('.document-row .status')
  await expect(page.getByText('unselected.pdf', { exact: true })).toBeVisible()
  await expect(documentStatus).toHaveText('解析中')
  await expect(page.locator('.auto-refresh')).toBeVisible()
  await retryFinished.promise
  await expect(page.locator('.toast.error')).toBeHidden()

  await finalRefreshFinished.promise
  await expect(documentStatus).toHaveText('已完成')
  await expect(page.locator('.auto-refresh')).toBeHidden()
  expect(runRequests).toBe(0)

  const requestsAfterCompletion = documentRequests
  await page.waitForTimeout(3500)
  expect(documentRequests).toBe(requestsAfterCompletion)
})

test('result tabs load and mount on first use, then preserve their state', async ({ page }) => {
  const document = documentItem('lazy-document', 'lazy-result.pdf')
  const run = parseRun('lazy-run', document.id, 'Lazy provider')
  let artifactRequests = 0
  let assetRequests = 0
  let pageRequests = 0
  let sequentialBlockRequests = 0
  let layoutBlockRequests = 0
  const assetContentRequests = new Map<string, number>()

  await page.route('**/api/v1/**', async route => {
    const url = new URL(route.request().url())
    const path = url.pathname
    if (path === '/api/v1/session') return fulfillJson(route, userSession)
    if (path === '/api/v1/parse-execution') {
      return fulfillJson(route, { workerEnabled: true, providerCredentialMissing: false })
    }
    if (path === '/api/v1/documents') return fulfillJson(route, { items: [document] })
    if (path === `/api/v1/documents/${document.id}/parse-runs`) return fulfillJson(route, [run])
    if (path === `/api/v1/parse-runs/${run.id}/artifacts`) {
      artifactRequests += 1
      return fulfillJson(route, [{
        id: 'markdown-artifact', type: 'markdown', name: 'result.md', mediaType: 'text/markdown',
        sizeBytes: 120, sha256: 'artifact-sha', createdAt: '2026-08-29T00:00:01Z',
      }])
    }
    if (path === `/api/v1/parse-runs/${run.id}/assets`) {
      assetRequests += 1
      return fulfillJson(route, [
        { id: 'asset-a', name: 'used-by-block.svg', mediaType: 'image/svg+xml', sizeBytes: 100, sha256: 'asset-a-sha', width: 20, height: 20 },
        { id: 'asset-b', name: 'resource-only.svg', mediaType: 'image/svg+xml', sizeBytes: 100, sha256: 'asset-b-sha', width: 20, height: 20 },
      ])
    }
    if (path === `/api/v1/parse-runs/${run.id}/pages`) {
      pageRequests += 1
      return fulfillJson(route, [{ number: 1, width: 100, height: 140, unit: 'pt' }])
    }
    if (path === `/api/v1/parse-runs/${run.id}/blocks`) {
      if (url.searchParams.has('pageNumber')) {
        layoutBlockRequests += 1
        return fulfillJson(route, { items: [{
          id: 'layout-block', sequence: 0, pageNumber: 1, type: 'image', assetId: 'asset-a',
          boundingBox: { x0: 0.1, y0: 0.1, x1: 0.5, y1: 0.4 },
        }] })
      }
      sequentialBlockRequests += 1
      return fulfillJson(route, {
        items: Array.from({ length: 60 }, (_, sequence) => ({
          id: `block-${sequence}`, sequence, pageNumber: 1, type: sequence === 0 ? 'image' : 'text',
          assetId: sequence === 0 ? 'asset-a' : undefined,
          content: `Block ${sequence} carries enough text to make the preserved panel scrollable.`,
        })),
      })
    }
    if (path === `/api/v1/parse-runs/${run.id}/markdown/preview`) {
      return route.fulfill({ status: 200, contentType: 'text/html', body: '<p>Rendered document</p>' })
    }
    const assetMatch = path.match(/\/assets\/(asset-[ab])\/content$/)
    if (assetMatch) {
      const assetId = assetMatch[1]
      assetContentRequests.set(assetId, (assetContentRequests.get(assetId) ?? 0) + 1)
      return route.fulfill({
        status: 200,
        contentType: 'image/svg+xml',
        body: '<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20"></svg>',
      })
    }
    return fulfillJson(route, { title: 'Unexpected mock request' }, 404)
  })

  await page.goto('/')
  await page.getByText(document.originalFileName, { exact: true }).click()
  await page.getByText(run.providerType, { exact: true }).click()
  await expect(page.locator('[data-result-panel="document"]')).toBeVisible()
  await expect(page.locator('[data-result-panel="document"] iframe')).toHaveAttribute('src', `/api/v1/parse-runs/${run.id}/markdown/preview`)

  expect(artifactRequests).toBe(1)
  expect(assetRequests).toBe(0)
  expect(pageRequests).toBe(0)
  expect(sequentialBlockRequests).toBe(0)
  expect(layoutBlockRequests).toBe(0)
  await expect(page.locator('[data-result-panel="blocks"]')).toHaveCount(0)
  await expect(page.locator('[data-result-panel="layout"]')).toHaveCount(0)
  await expect(page.locator('[data-result-panel="resources"]')).toHaveCount(0)
  expect(assetContentRequests.get('asset-b') ?? 0).toBe(0)

  const tabs = page.locator('.result-tabs')
  await tabs.getByRole('button', { name: /结构/ }).click()
  const blockList = page.locator('[data-result-panel="blocks"] .block-list')
  await expect(blockList.locator('article')).toHaveCount(60)
  await expect(blockList.locator('img')).toHaveAttribute('loading', 'lazy')
  expect(assetRequests).toBe(1)
  expect(sequentialBlockRequests).toBe(1)
  expect(pageRequests).toBe(0)
  expect(assetContentRequests.get('asset-b') ?? 0).toBe(0)

  await blockList.evaluate(element => { element.scrollTop = 320 })
  const preservedScrollTop = await blockList.evaluate(element => element.scrollTop)
  expect(preservedScrollTop).toBeGreaterThan(0)

  await tabs.getByRole('button', { name: /资源/ }).click()
  const resources = page.locator('[data-result-panel="resources"]')
  await expect(resources).toBeVisible()
  await expect(resources.locator('img')).toHaveCount(2)
  await expect(resources.locator('img').first()).toHaveAttribute('loading', 'lazy')
  expect(assetRequests).toBe(1)
  expect(artifactRequests).toBe(1)

  await tabs.getByRole('button', { name: /结构/ }).click()
  await expect(blockList).toBeVisible()
  expect(await blockList.evaluate(element => element.scrollTop)).toBe(preservedScrollTop)
  expect(sequentialBlockRequests).toBe(1)

  await tabs.getByRole('button', { name: /版面/ }).click()
  await expect(page.locator('[data-result-panel="layout"] .layout-map')).toBeVisible()
  expect(pageRequests).toBe(1)
  expect(layoutBlockRequests).toBe(1)
  expect(assetRequests).toBe(1)

  await tabs.getByRole('button', { name: /资源/ }).click()
  expect(assetRequests).toBe(1)
  expect(artifactRequests).toBe(1)
  expect(pageRequests).toBe(1)
  expect(sequentialBlockRequests).toBe(1)
  expect(layoutBlockRequests).toBe(1)
})

test('result reads ignore stale success, failure, and loading completion', async ({ page }) => {
  const document = documentItem('stale-result-document', 'stale-result.pdf')
  const runs = [
    parseRun('run-a', document.id, 'Provider A'),
    parseRun('run-b', document.id, 'Provider B'),
    parseRun('run-c', document.id, 'Provider C'),
    parseRun('run-d', document.id, 'Provider D'),
  ]
  const aStarted = deferred(); const releaseA = deferred(); const aFinished = deferred()
  const bStarted = deferred(); const releaseB = deferred(); const bFinished = deferred()
  const cStarted = deferred(); const releaseC = deferred(); const cFinished = deferred()
  let resultMetadataRequests = 0
  let unexpectedResultRequests = 0

  const markdownArtifact = (runId: string) => [{
    id: `artifact-${runId}`, type: 'markdown', name: `${runId}.md`, mediaType: 'text/markdown',
    sizeBytes: 100, sha256: `${runId}-sha`, createdAt: '2026-08-29T00:00:01Z',
  }]

  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/v1/session') return fulfillJson(route, userSession)
    if (path === '/api/v1/parse-execution') {
      return fulfillJson(route, { workerEnabled: true, providerCredentialMissing: false })
    }
    if (path === '/api/v1/documents') return fulfillJson(route, { items: [document] })
    if (path === `/api/v1/documents/${document.id}/parse-runs`) return fulfillJson(route, runs)
    if (path === '/api/v1/parse-runs/run-a/artifacts') {
      resultMetadataRequests += 1; aStarted.resolve(); await releaseA.promise
      await fulfillJson(route, markdownArtifact('run-a')); aFinished.resolve(); return
    }
    if (path === '/api/v1/parse-runs/run-b/artifacts') {
      resultMetadataRequests += 1; bStarted.resolve(); await releaseB.promise
      await fulfillJson(route, markdownArtifact('run-b')); bFinished.resolve(); return
    }
    if (path === '/api/v1/parse-runs/run-c/artifacts') {
      resultMetadataRequests += 1; cStarted.resolve(); await releaseC.promise
      await fulfillJson(route, { title: 'Delayed stale result failure' }, 503); cFinished.resolve(); return
    }
    if (path === '/api/v1/parse-runs/run-d/artifacts') {
      resultMetadataRequests += 1
      return fulfillJson(route, markdownArtifact('run-d'))
    }
    if (/\/api\/v1\/parse-runs\/run-[a-d]\/markdown\/preview$/.test(path)) {
      return route.fulfill({ status: 200, contentType: 'text/html', body: '<p>Rendered document</p>' })
    }
    if (/\/api\/v1\/parse-runs\/run-[a-d]\/(pages|assets|blocks)$/.test(path)) {
      unexpectedResultRequests += 1
    }
    return fulfillJson(route, { title: 'Unexpected mock request' }, 404)
  })

  await page.goto('/')
  await page.getByText(document.originalFileName, { exact: true }).click()

  await page.getByText('Provider A', { exact: true }).click()
  await aStarted.promise
  await expect(page.locator('[data-result-loading="document"]')).toBeVisible()

  await page.getByText('Provider B', { exact: true }).click()
  await bStarted.promise
  releaseA.resolve(); await aFinished.promise; await settleUi(page)
  await expect(page.locator('[data-result-loading="document"]')).toBeVisible()
  await expect(page.locator('[data-result-panel="document"] iframe')).toHaveCount(0)
  await expect(page.locator('.toast.error')).toBeHidden()

  releaseB.resolve(); await bFinished.promise
  await expect(page.locator('[data-result-panel="document"] iframe')).toHaveAttribute('src', '/api/v1/parse-runs/run-b/markdown/preview')

  await page.getByText('Provider C', { exact: true }).click()
  await cStarted.promise
  await page.getByText('Provider D', { exact: true }).click()
  await expect(page.locator('[data-result-panel="document"] iframe')).toHaveAttribute('src', '/api/v1/parse-runs/run-d/markdown/preview')
  releaseC.resolve(); await cFinished.promise; await settleUi(page)
  await expect(page.locator('[data-result-panel="document"] iframe')).toHaveAttribute('src', '/api/v1/parse-runs/run-d/markdown/preview')
  await expect(page.locator('.toast.error')).toBeHidden()

  expect(resultMetadataRequests).toBe(4)
  expect(unexpectedResultRequests).toBe(0)
})

test('administrator can use the document workspace and administration area', async ({ page }) => {
  // A retry runs against the deployment the previous attempt already wrote to, and nothing here is
  // cleaned up afterwards. Names carry a run stamp so an attempt never matches a leftover.
  const stamp = Date.now().toString(36)
  const providerName = `Contract provider ${stamp}`
  const correctedName = `Corrected provider ${stamp}`
  const fileName = `e2e-sample-${stamp}.pdf`

  // The workspace and the administration area are distinct routes of one Host, and both send an
  // unauthenticated visitor to their own sign-in page.
  await page.goto('/')
  await expect(page).toHaveURL(/\/signin/)

  await page.goto('/admin')
  await expect(page).toHaveURL(/\/admin\/signin/)
  await page.locator('form input[name="username"]').fill(administratorUsername)
  await page.locator('form input[type="password"]').fill(administratorPassword)
  await page.locator('form button').click()

  await expect(page).toHaveURL(/\/admin$/)
  await expect(page.getByText('ADMINISTRATION', { exact: true })).toBeVisible()
  await expect(page.getByText('PROVIDERS', { exact: true })).toBeVisible()
  await expect(page.getByText('API CLIENTS', { exact: true })).toBeVisible()
  // Where documents and business data live are settings like any other, and a deployment that could
  // not reach them from here would have to be recreated to move either.
  await expect(page.getByText('STORAGE', { exact: true })).toBeVisible()
  await expect(page.getByText('DATABASE', { exact: true })).toBeVisible()

  // Which build is running has to be readable by an administrator who never opens a terminal, which
  // is the whole reason it is on the page rather than only in `docker image inspect`. What the page
  // owes is that the string is the service's own, in full, copyable: the abbreviation next to it is
  // for reading. Whether that string names a commit depends on the build arguments rather than on
  // this page, and the container job checks it where it is decided.
  const reported = await page.evaluate(async () => (await (await fetch('/api/v1/system/info')).json()).version)
  expect(reported).toBeTruthy()
  await expect(page.locator('.page-header code')).toHaveAttribute('title', reported)

  const providers = page.locator('section').filter({ has: page.getByText('PROVIDERS', { exact: true }) })

  // The image arrives with the official endpoint already configured, so a first-run administrator
  // supplies a token rather than assembling an address and a model from documentation. It is the
  // default and deliberately has no credential, and both have to be legible on this page: the token
  // is the one thing nobody but this deployment's administrator can provide.
  // Being the default is asserted against a fresh Host in OfficialProviderSeedTests rather than
  // here, because a retry runs against the deployment the previous attempt already made its own
  // Provider the default in.
  const officialRow = providers.locator('.admin-list > div').filter({ hasText: 'official' })
  await expect(officialRow.getByText('缺少凭据', { exact: true })).toBeVisible()
  await expect(officialRow.locator('small')).toContainText('https://mineru.net/')
  await expect(officialRow.locator('small')).toContainText('模型 vlm')
  // A deployment with no Provider cannot parse anything, so the administration area has to be able
  // to create one and mark it default without a command line. It is also the first thing on the page
  // and opens itself while there are none, so this makes sure the form is open rather than clicking:
  // a click on an open disclosure closes it.
  const createDetails = providers.locator('details').first()
  if (!await createDetails.evaluate(details => (details as HTMLDetailsElement).open)) {
    await providers.getByText('新增提供方').click()
  }
  const createForm = createDetails.locator('.form-grid')
  await createForm.locator('input').first().fill(providerName)

  // The hosted service has one published address, and an administrator who has to retype it from
  // memory is being asked to get it wrong. Selecting the type fills it in, and the value comes from
  // the service rather than from this bundle, so this also proves the descriptors were served.
  await createForm.locator('select').selectOption('mineru-cloud')
  await expect(createForm.locator('input[type="url"]')).toHaveValue('https://mineru.net')
  // Each type reads one of the two optional settings, so the other is not offered at all: a field
  // that changes nothing about the request is worse than a missing one.
  await expect(createForm.getByText('模型（可选）')).toBeVisible()
  await expect(createForm.getByText('后端（可选）')).toBeHidden()

  await createForm.locator('select').selectOption('mineru-local')
  // A self-hosted address is the deployment's own, so switching away from the hosted type takes the
  // suggestion back out rather than leaving a wrong address that looks deliberate.
  await expect(createForm.locator('input[type="url"]')).toHaveValue('')
  await createForm.locator('input[type="url"]').fill(refusingProviderBaseUrl)
  await createForm.locator('input[type="checkbox"]').check()
  await createForm.getByRole('button', { name: '创建' }).click()

  const providerRow = providers.locator('.admin-list > div').filter({ hasText: providerName })
  await expect(providerRow.getByText('默认', { exact: true })).toBeVisible()
  await page.screenshot({ path: 'test-results/administration.png', fullPage: true })

  await page.getByRole('link', { name: '文档工作台' }).click()
  await expect(page).toHaveURL(/\/$/)
  await expect(page.getByText('WORKSPACE', { exact: true })).toBeVisible()

  await page.locator('input[type="file"]').setInputFiles({
    name: fileName,
    mimeType: 'application/pdf',
    buffer: Buffer.from('%PDF-1.4\n% StructaDoc browser contract sample\n%%EOF\n'),
  })
  await expect(page.getByText(fileName, { exact: true })).toBeVisible()

  // Starting a parse names no Provider, so this only works because the default set above is in
  // force. It is the step that fails when a deployment has configuration but no default.
  await page.getByText(fileName, { exact: true }).click()
  await page.getByRole('button', { name: '开始新解析' }).click()
  const runStatus = page.locator('.run-list .status').first()
  await expect(runStatus).toBeVisible()
  await page.locator('.run-list .run-open').first().click()

  // Nothing is clicked from here. The workspace polls while work is unfinished, so reaching a final
  // status on screen proves the Worker ran and the auto-refresh reported it. The error text
  // appearing without a second click proves the poll kept the selected run rather than dropping it.
  await expect(page.locator('.auto-refresh')).toBeVisible()
  await expect(runStatus).toHaveText('失败', { timeout: 60_000 })
  await expect(page.locator('.inline-error')).toBeVisible()
  await expect(page.locator('.auto-refresh')).toBeHidden()

  // The first useful panel reads Artifact metadata, then falls back to Blocks and Asset metadata
  // because this failed run has no Markdown. Pages stay deferred until Layout is opened. What the
  // visible panel owes is to say the result is empty rather than throwing on it.
  const resultTabs = page.locator('.result-tabs')
  await expect(resultTabs.getByRole('button', { name: /版面/ })).toBeVisible()
  // Without a Markdown Artifact there is nothing on the document tab, so the panel opens on the
  // structure instead of on an empty frame.
  await expect(resultTabs.getByRole('button', { name: /结构/ })).toHaveClass(/active/)
  await expect(page.getByText('结果尚未生成或不含内容块。')).toBeVisible()

  await page.screenshot({ path: 'test-results/workspace.png', fullPage: true })

  // A failed run is still a record the user has to be able to get rid of, including when it is the
  // only one the Document has. What is left afterwards is the Document, unparsed and parseable
  // again — the cleanup itself is covered against storage by UserWorkspaceFeatureTests.
  page.once('dialog', dialog => dialog.accept())
  await page.locator('.run-list .run-delete').first().click()
  await expect(page.getByText('尚未创建解析任务。')).toBeVisible()
  await expect(page.locator('.document-row.selected .status')).toHaveText('未解析')

  // A deep administration link survives a full reload, so the Host serves the SPA shell for
  // client-side routes rather than only for `/`.
  await page.goto('/admin')
  await expect(page.getByText('ADMINISTRATION', { exact: true })).toBeVisible()

  // A configuration that has been used keeps its parse history, so it is disabled rather than
  // deleted. Both answers have to reach the administrator rather than fail silently.
  page.once('dialog', dialog => dialog.accept())
  await providerRow.getByRole('button', { name: '删除' }).click()
  await expect(page.locator('.toast.error')).toBeVisible()
  await expect(providerRow).toBeVisible()

  // Correcting a configuration in place is the other half: without it a wrong address or credential
  // could only be replaced by creating another Provider.
  await providerRow.getByRole('button', { name: '编辑' }).click()
  const editor = providers.locator('.provider-editor')
  await editor.locator('input').first().fill(correctedName)
  await editor.getByRole('button', { name: '保存' }).click()
  await expect(providers.getByText(correctedName)).toBeVisible()

  // Filling the form in is the administrator saying they want this Provider used, so saving is what
  // enables it. A stopped Provider that is edited and saved comes back enabled without a second
  // click, and the form offers no switch to leave it off by accident.
  const correctedRow = providers.locator('.admin-list > div').filter({ hasText: correctedName })
  await correctedRow.getByRole('button', { name: '停用' }).click()
  await expect(correctedRow.getByText('停用', { exact: true })).toBeVisible()
  await correctedRow.getByRole('button', { name: '编辑' }).click()
  await expect(editor.getByText('保存后会自动启用')).toBeVisible()
  await editor.getByRole('button', { name: '保存' }).click()
  await expect(correctedRow.getByText('启用', { exact: true })).toBeVisible()
})
