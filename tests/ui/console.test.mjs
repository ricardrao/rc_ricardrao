import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { JSDOM } from 'jsdom';

const base = process.env.TEST_UI_URL;
test('console registers a supplier, publishes a capability, connects and sends through real APIs', { skip: !base, timeout: 60000 }, async () => {
  const html = await (await fetch(base)).text();
  const zh = await (await fetch(base + '/locales/zh-CN.json')).json();
  const en = await (await fetch(base + '/locales/en.json')).json();
  const dom = new JSDOM(html, { url: base + '/#overview', pretendToBeVisual: true });
  const window = dom.window;
  window.HTMLDialogElement.prototype.showModal = function () { this.setAttribute('open', ''); };
  window.HTMLDialogElement.prototype.close = function () { this.removeAttribute('open'); };
  const originals = {};
  for (const name of ['window', 'document', 'location', 'localStorage', 'HTMLFormElement', 'FormData']) {
    originals[name] = globalThis[name]; globalThis[name] = name === 'window' ? window : window[name];
  }
  const originalFetch = globalThis.fetch;
  globalThis.fetch = (path, options = {}) => originalFetch(new URL(path, base), { ...options, headers: { ...options.headers, Origin: base } });
  const originalInterval = globalThis.setInterval;
  const originalTimeout = globalThis.setTimeout;
  const intervals = [];
  const timeouts = [];
  globalThis.setInterval = (...args) => { const timer = originalInterval(...args); timer.unref(); intervals.push(timer); return timer; };
  globalThis.setTimeout = (...args) => { const timer = originalTimeout(...args); timeouts.push(timer); return timer; };
  const byId = id => window.document.getElementById(id);
  async function until(predicate) {
    const deadline = Date.now() + 12000;
    while (!predicate()) {
      if (Date.now() > deadline) throw new Error('UI condition timed out: ' + window.document.getElementById('main').textContent.slice(0, 700) + ' / ' + byId('notice').textContent);
      await new Promise(resolve => setTimeout(resolve, 30));
    }
  }
  async function route(name, selector) { window.location.hash = '#' + name; await until(() => window.document.querySelector(selector)); }
  const submit = form => form.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  try {
    await import('../../src/NotificationHub.Api/wwwroot/console.js?test=' + Date.now());
    await until(() => window.document.querySelector('.stats'));
    assert.equal(window.document.querySelectorAll('.nav-link').length, 7);
    await route('providers', '#register-provider');
    const suffix = Date.now().toString().slice(-7);
    byId('providerName').value = 'Console supplier ' + suffix;
    byId('language').value = 'en'; byId('language').dispatchEvent(new window.Event('change'));
    await until(() => window.document.querySelector('h1')?.textContent === en.providers);
    assert.equal(byId('providerName').value, 'Console supplier ' + suffix);
    byId('language').value = 'zh-CN'; byId('language').dispatchEvent(new window.Event('change'));
    await until(() => window.document.querySelector('h1')?.textContent === zh.providers);
    submit(byId('register-provider'));
    await until(() => window.document.querySelector('.provider-item.selected')?.textContent.includes('Console supplier ' + suffix));
    const providerId = window.document.querySelector('.provider-item.selected').dataset.id;
    byId('eventType').value = 'console.contact.v1';
    const description = 'Synchronize a contact after payment.\n<b>Plain text description</b>';
    byId('description').value = description;
    assert.equal(byId('description').maxLength, 2000);
    byId('url').value = 'http://127.0.0.1:8081/json';
    submit(byId('version-form'));
    await until(() => window.document.querySelector('button[data-action=publish]'));
    assert.equal(window.document.querySelector('.version-list .operation-description').textContent, description);
    assert.equal(window.document.querySelector('.version-list .operation-description b'), null);
    window.document.querySelector('button[data-action=preview]').click();
    submit(byId('preview-form'));
    await until(() => !byId('preview-result').hidden);
    assert.equal(JSON.parse(JSON.parse(byId('preview-result').textContent).body).customerId, 'c123');
    byId('close-dialog').click();
    window.document.querySelector('button[data-action=publish]').click();
    await until(() => !window.document.querySelector('button[data-action=publish]') && window.document.querySelector('.version-list .badge.good'));
    await route('connections', '#connection-form');
    byId('providerId').value = providerId;
    byId('connectionName').value = 'Console connection ' + suffix;
    submit(byId('connection-form'));
    await until(() => [...window.document.querySelectorAll('td .primary-cell')].some(x => x.textContent === 'Console connection ' + suffix));
    const row = [...window.document.querySelectorAll('tr')].find(x => x.textContent.includes('Console connection ' + suffix));
    const connectionId = row.querySelector('[data-action=connection-health]').dataset.id;
    await route('send', '#send-form');
    byId('eventType').value = 'console.contact.v1';
    byId('eventType').dispatchEvent(new window.Event('change', { bubbles: true }));
    const checkbox = [...window.document.querySelectorAll('input[name=target]')].find(x => x.value.startsWith(connectionId));
    assert.ok(checkbox); checkbox.checked = true;
    assert.equal(checkbox.closest('label').querySelector('.operation-description').textContent, description);
    assert.equal(checkbox.closest('label').querySelector('.operation-description b'), null);
    submit(byId('send-form'));
    await until(() => !byId('receipt').hidden && !byId('send-form').querySelector('button[type=submit]').disabled);
    const receiptLink = byId('receipt').querySelector('a').getAttribute('href');
    submit(byId('send-form'));
    await until(() => byId('receipt').textContent.includes(zh.duplicateReceipt));
    assert.equal(byId('receipt').querySelector('a').getAttribute('href'), receiptLink);
    await route(receiptLink.slice(1), '.detail-grid');
    for (let poll = 0; poll < 20 && !window.document.querySelector('.badge.good'); poll++) {
      await new Promise(resolve => setTimeout(resolve, 500));
      window.document.querySelector('[data-action=refresh]').click();
      await until(() => window.document.querySelector('.detail-grid'));
    }
    assert.ok(window.document.querySelector('.badge.good'));
    assert.match(window.document.getElementById('main').textContent, /Console connection/);
    await route('notifications', '#status-filter');
    assert.ok(window.document.querySelector(`a[href="${receiptLink}"]`));
    await route('tickets', '.page-heading');
    await until(() => window.document.querySelector('h1')?.textContent === zh.tickets);
    byId('language').value = 'en'; byId('language').dispatchEvent(new window.Event('change'));
    await until(() => window.document.querySelector('h1')?.textContent === en.tickets);
    byId('language').value = 'zh-CN'; byId('language').dispatchEvent(new window.Event('change'));
    await until(() => window.document.querySelector('h1')?.textContent === zh.tickets);
    await route('settings', '.callout');
    assert.ok(window.document.getElementById('main').textContent.includes(zh.demoSettings));
    assert.equal(window.localStorage.getItem('businessKey'), null);
  } finally {
    intervals.forEach(clearInterval); timeouts.forEach(clearTimeout); globalThis.fetch = originalFetch; globalThis.setInterval = originalInterval; globalThis.setTimeout = originalTimeout;
    for (const [name, value] of Object.entries(originals)) { if (value === undefined) delete globalThis[name]; else globalThis[name] = value; }
    window.close();
  }
});

test('both locale files contain exactly the same UI messages', async () => {
  const root = new URL('../../src/NotificationHub.Api/wwwroot/locales/', import.meta.url);
  const en = JSON.parse(await readFile(new URL('en.json', root), 'utf8'));
  const zh = JSON.parse(await readFile(new URL('zh-CN.json', root), 'utf8'));
  assert.deepEqual(Object.keys(en).sort(), Object.keys(zh).sort());
  assert.ok(Object.values(en).every(x => typeof x === 'string' && x.length > 0));
});
