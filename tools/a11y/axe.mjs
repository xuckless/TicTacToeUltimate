// Accessibility scan (WCAG 2.0/2.1/2.2 A + AA rules) of the main routes with axe-core in a real browser.
// Fails (exit 1) on serious or critical violations. Usage: node axe.mjs http://localhost:5028 / /play /settings
// Env: AXE_DEV_USER=<name> signs in through the dev sign-in card first; CHROME=<path> uses an installed Chrome.
import { chromium } from 'playwright';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const axeSource = readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8');
const [base = 'http://localhost:5028', ...routes] = process.argv.slice(2);
const paths = routes.length ? routes : ['/local'];
const browser = await chromium.launch(process.env.CHROME ? { executablePath: process.env.CHROME } : {});
let failed = 0;
for (const scheme of ['dark', 'light']) {
  const page = await (await browser.newContext({ viewport: { width: 390, height: 844 }, colorScheme: scheme })).newPage();
  if (process.env.AXE_DEV_USER) {
    await page.goto(base + '/signin'); await page.waitForTimeout(6000);
    await page.getByText(process.env.AXE_DEV_USER).first().click(); await page.waitForTimeout(4000);
  }
  for (const path of paths) {
    await page.goto(base + path); await page.waitForTimeout(5000);
    await page.addScriptTag({ content: axeSource });
    const result = await page.evaluate(async () => await window.axe.run(document, {
      runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'] },
    }));
    const bad = result.violations.filter(v => v.impact === 'serious' || v.impact === 'critical');
    console.log(`${scheme} ${path}: ${result.violations.length} violations (${bad.length} serious/critical)`);
    for (const v of result.violations) console.log(`  [${v.impact}] ${v.id}: ${v.help} (${v.nodes.length}) ${v.nodes.slice(0, 2).map(n => n.target.join(' ')).join(' | ')}`);
    failed += bad.length;
  }
}
await browser.close();
process.exit(failed > 0 ? 1 : 0);
