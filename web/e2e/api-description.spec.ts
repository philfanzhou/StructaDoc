import { expect, test } from '@playwright/test'

// The unit tests prove the document is generated and served. What only a browser against the
// published image can show is that the page renders it with no network but this one: the viewer's
// assets ship inside the Host assembly rather than arriving from a CDN, because a deployment on an
// isolated network is the deployment this product is built for, and a page that silently needed the
// internet would be blank in exactly those installations and nowhere else.
test('the API description renders from the image alone', async ({ page }) => {
  const external: string[] = []
  page.on('request', (request) => {
    const url = new URL(request.url())
    if (url.protocol.startsWith('http') && url.host !== '127.0.0.1:8080') {
      external.push(request.url())
    }
  })

  await page.goto('/api/v1/docs/')

  // The document was fetched and rendered, rather than the page having merely arrived.
  await expect(page.getByRole('heading', { name: /StructaDoc API/ })).toBeVisible()
  await expect(page.getByText('Documents', { exact: true }).first()).toBeVisible()
  await expect(
    page.locator('.opblock-summary-path').filter({ hasText: '/api/v1/documents' }).first(),
  ).toBeVisible()
  expect(external).toEqual([])
})
