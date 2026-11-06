import { parseObject, notificationState, latestPublished, submission, operationDefinition } from './console-domain.mjs';

const state = { language: localStorage.getItem('hub.language') || 'zh-CN', words: {}, config: {}, keys: { business: '', provider: '' }, selectedProvider: null, page: 1, filter: '', targets: [], generation: 0 };
const $ = id => document.getElementById(id);
const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const t = key => state.words[key] || key;
const e = key => escape(t(key));
const pretty = value => JSON.stringify(value, null, 2);
const short = id => escape((id || '').slice(0, 8));
const date = value => value ? escape(new Date(value).toLocaleString(state.language)) : '-';
const badge = (status, context = '') => `<span class="badge ${['succeeded', 'healthy', 'published', 'enabled', 'closed'].includes(status) ? 'good' : ['pending', 'processing', 'retry_wait', 'degraded', 'recovering', 'draft'].includes(status) ? 'warn' : ['stopped', 'expired', 'open'].includes(status) ? 'bad' : 'neutral'}">${e(status === 'open' && context === 'health' ? 'circuitOpen' : status)}</span>`;
const button = (label, action, id = '', classes = 'small', extra = '') => `<button type="button" class="button ${classes}" data-action="${action}" data-id="${escape(id)}" ${extra}>${e(label)}</button>`;
const link = (label, route, primary = false) => `<a class="button ${primary ? 'primary' : ''}" href="#${route}">${e(label)}</a>`;
const empty = (message = 'empty', action = '') => `<div class="empty"><div class="empty-icon">&#9633;</div><p>${e(message)}</p>${action}</div>`;
const input = (name, label, value = '', attrs = '', hint = '') => `<div class="field"><label for="${name}">${e(label)}</label><input id="${name}" name="${name}" value="${escape(value)}" ${attrs}>${hint ? `<small>${e(hint)}</small>` : ''}</div>`;
const textarea = (name, label, value, cls = '', hint = '') => `<div class="field full"><label for="${name}">${e(label)}</label><textarea id="${name}" name="${name}" class="${cls}" spellcheck="false" required>${escape(value)}</textarea>${hint ? `<small>${e(hint)}</small>` : ''}</div>`;
const select = (name, label, options, value = '') => `<div class="field"><label for="${name}">${e(label)}</label><select id="${name}" name="${name}" required>${options.map(([key, text]) => `<option value="${escape(key)}" ${key === value ? 'selected' : ''}>${escape(text)}</option>`).join('')}</select></div>`;
const heading = (name, action = '') => `<div class="page-heading"><div><h1>${e(name)}</h1><p class="subtitle">${e(name + 'Desc')}</p></div><div class="toolbar">${action}</div></div>`;
const samplePayload = { contact_id: 'c123', status: 'paid' };
const sampleInputs = [{ name: 'contact_id', type: 'string', required: true }, { name: 'status', type: 'string', required: true }];
const sampleMapping = { customerId: { source: 'data.contact_id' }, state: { source: 'data.status', values: { paid: 1 } } };
const sampleSuccess = { httpStatuses: [200], bodyPath: 'code', expectedValue: 0 };
const routeName = () => (location.hash.slice(1) || 'overview').split('/')[0];
const routes = [['overview', '&#9638;'], ['providers', '&#9672;'], ['connections', '&#8644;'], ['send', '&#8599;'], ['notifications', '&#9776;'], ['tickets', '&#9873;'], ['settings', '&#9881;']];
let noticeTimer;
async function mapLimited(values, transform) {
  const results = [];
  for (let index = 0; index < values.length; index += 4) results.push(...await Promise.all(values.slice(index, index + 4).map(transform)));
  return results;
}

async function api(path, { method = 'GET', body, role = 'business', key } = {}) {
  let response;
  if (state.config.demo) {
    response = await fetch('/local-api', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Console-Request': '1' }, body: JSON.stringify({ path, method, body, role, key, language: state.language }) });
  } else {
    if (!state.keys[role]) throw new Error('accessRequired');
    const headers = { 'X-Api-Key': state.keys[role], 'Accept-Language': state.language };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (key) headers['Idempotency-Key'] = key;
    response = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  }
  const text = await response.text();
  let result;
  try { result = text ? JSON.parse(text) : null; } catch { throw new Error(t('requestFailed') + ' (' + response.status + ')'); }
  if (!response.ok) throw new Error(result?.message || result?.error || t('requestFailed') + ' (' + response.status + ')');
  return result;
}
function notify(message, error = false) {
  clearTimeout(noticeTimer); $('notice').textContent = t(message); $('notice').classList.toggle('error', error); $('notice').hidden = false;
  noticeTimer = setTimeout(() => { $('notice').hidden = true; }, 6000);
}
function modal(title, html) {
  $('dialog-title').textContent = t(title); $('dialog-content').innerHTML = html;
  if (!$('dialog').open) $('dialog').showModal();
}
function navigation() {
  $('navigation').innerHTML = routes.map(([name, icon]) => `<a class="nav-link ${routeName() === name ? 'active' : ''}" href="#${name}" ${routeName() === name ? 'aria-current="page"' : ''}><span class="nav-icon" aria-hidden="true">${icon}</span>${e(name)}</a>`).join('');
  document.querySelectorAll('[data-i18n]').forEach(node => { node.textContent = t(node.dataset.i18n); });
  $('environment').textContent = state.config.demo ? t('environment') : 'Notification Hub';
  $('mode').textContent = t(state.config.demo ? 'demoMode' : 'secureMode');
  $('breadcrumb').textContent = t(routeName());
  $('close-dialog').setAttribute('aria-label', t('close'));
}
function notificationTable(items) {
  if (!items.length) return empty('emptyNotifications', link('newNotification', 'send', true));
  return `<div class="table-wrap"><table><thead><tr><th>${e('eventType')}</th><th>${e('state')}</th><th>${e('targetCount')}</th><th>${e('createdAt')}</th><th>${e('actions')}</th></tr></thead><tbody>${items.map(item => `<tr><td><span class="primary-cell">${escape(item.notification.eventType)}</span><span class="secondary-cell code">${short(item.notification.id)} &middot; ${escape(item.notification.idempotencyKey)}</span></td><td>${badge(notificationState(item.deliveries))}</td><td>${item.deliveries.filter(d => d.status === 'succeeded').length} / ${item.deliveries.length}</td><td>${date(item.notification.createdAt)}</td><td><a class="button link" href="#notifications/${item.notification.id}">${e('detail')} &rarr;</a></td></tr>`).join('')}</tbody></table></div>`;
}
async function overview() {
  const [metrics, recent, tickets] = await Promise.all([api('/v1/metrics'), api('/v1/notifications?pageSize=5'), api('/v1/tickets')]);
  const counts = Object.fromEntries(metrics.deliveries.map(x => [x.status, Number(x.count)]));
  const stats = [['totalDeliveries', Object.values(counts).reduce((a, b) => a + b, 0), 'allTime', 'notifications'], ['successCount', counts.succeeded || 0, 'acceptedBySupplier', 'notifications'], ['pendingCount', ['pending', 'processing', 'retry_wait'].reduce((a, s) => a + (counts[s] || 0), 0), 'autoRetry', 'notifications'], ['openTickets', tickets.filter(x => x.status === 'open').length, 'requiresAttention', 'tickets']];
  return heading('overview', button('refresh', 'refresh') + link('newNotification', 'send', true)) + `<div class="stats">${stats.map(([label, value, foot, route]) => `<a href="#${route}" class="stat"><div class="stat-label">${e(label)}<span class="stat-icon">&#8599;</span></div><div class="stat-value" style="color:var(--ink)">${value}</div><div class="stat-foot">${e(foot)}</div></a>`).join('')}</div><div class="panel"><div class="panel-header"><h2>${e('recentNotifications')}</h2><a href="#notifications">${e('viewAll')} &rarr;</a></div>${notificationTable(recent.items)}</div><div class="overview-bottom"><div class="panel"><h2>${e('tickets')}</h2>${ticketTable(tickets.slice(0, 4))}</div><div class="panel"><h2>${e('quickStart')}</h2><ol class="help-list"><li><a href="#providers">${e('stepProvider').replace(/^1\. /, '')}</a></li><li><a href="#connections">${e('stepConnection').replace(/^2\. /, '')}</a></li><li><a href="#send">${e('stepSend').replace(/^3\. /, '')}</a></li></ol></div></div>`;
}
async function providers() {
  const rows = await api('/v1/providers', { role: 'provider' });
  const selected = rows.find(x => x.id === state.selectedProvider) || rows[0];
  state.selectedProvider = selected?.id;
  const versions = selected ? await api(`/v1/providers/${selected.id}/versions`, { role: 'provider' }) : [];
  const register = `<section class="panel"><h2>${e('registerProvider')}</h2><form id="register-provider"><div class="form-grid">${input('providerName', 'providerName', '', 'required maxlength="100" class="full" placeholder="' + e('providerNamePlaceholder') + '"')}</div><div class="form-actions start"><button class="button primary" type="submit">+ ${e('registerProvider')}</button></div></form><p class="muted">${e('registerHint')}</p><div class="provider-picker">${rows.map(row => `<button type="button" class="provider-item ${selected?.id === row.id ? 'selected' : ''}" data-action="select-provider" data-id="${row.id}"><span>${escape(row.name)}<small>${short(row.id)}</small></span>${badge(row.enabled ? 'enabled' : 'disabled')}</button>`).join('')}</div></section>`;
  if (!selected) return heading('providers') + `<div class="split">${register}<div class="panel">${empty('registerHint')}</div></div>`;
  const versionList = `<div class="version-list">${versions.length ? versions.map(v => `<div class="version-item"><div><strong>${escape(v.operation)}</strong><span class="secondary-cell">${e('version')} ${v.version} &middot; ${escape(JSON.parse(v.definition).eventType)}</span><p class="operation-description">${escape(JSON.parse(v.definition).description || t('noDescription'))}</p></div><div class="toolbar">${badge(v.status)}${button('preview', 'preview', v.id)}${v.status === 'draft' ? button('publish', 'publish', v.id) : ''}</div></div>`).join('') : empty('noVersions')}</div>`;
  return heading('providers', button('refresh', 'refresh')) + `<div class="split">${register}<section class="panel"><div class="panel-header"><h2>${escape(selected.name)}</h2>${button(selected.enabled ? 'disable' : 'enable', 'toggle-provider', selected.id, 'small', `data-enabled="${!selected.enabled}"`)}</div><p class="muted">${e('publishedHint')}</p>${versionList}<hr style="border:0;border-top:1px solid var(--line);margin:24px 0"><div class="panel-header"><h2>${e('createVersion')}</h2>${button('sample', 'sample-version')}</div><form id="version-form" data-provider="${selected.id}"><div class="form-grid">${input('operation', 'operation', 'contact.update', 'required maxlength="100"')}${input('eventType', 'eventType', 'contact.updated.v1', 'required')}<div class="field full"><label for="description">${e('description')}</label><textarea id="description" name="description" maxlength="2000" placeholder="${e('descriptionPlaceholder')}"></textarea><small>${e('descriptionHint')}</small></div>${input('url', 'endpointUrl', state.config.supplierUrl || 'https://api.supplier.example/contacts/update', 'type="url" required')}${select('method', 'httpMethod', ['POST', 'PUT', 'PATCH'].map(x => [x, x]), 'POST')}${select('encoding', 'encoding', [['json', 'JSON'], ['form', 'Form URL encoded']], 'json')}${select('authType', 'authType', [['none', t('none')], ['bearer', t('bearer')], ['header', t('headerAuth')]], 'none')}${textarea('inputs', 'inputSchema', pretty(sampleInputs))}${textarea('mapping', 'bodyMapping', pretty(sampleMapping), '', 'mappingHint')}</div><details class="advanced"><summary>${e('advanced')}</summary><div class="form-grid">${input('authHeader', 'authHeader', 'Authorization', 'required')}${input('idempotencyHeader', 'idempotencyHeader', 'Idempotency-Key', 'required')}${input('timeoutSeconds', 'timeoutSeconds', '15', 'type="number" min="1" max="30" required')}${input('maxAttempts', 'maxAttempts', '8', 'type="number" min="1" max="20" required')}${input('maxAgeHours', 'maxAgeHours', '24', 'type="number" min="1" max="168" required')}${input('idempotencyRetentionHours', 'retentionHours', '168', 'type="number" min="1" max="8760" required')}${input('requestsPerMinute', 'requestsPerMinute', '60', 'type="number" min="1" max="10000" required')}${textarea('headers', 'staticHeaders', '{}')}${textarea('successRule', 'successRule', pretty(sampleSuccess))}</div></details><div class="form-actions"><button type="submit" class="button primary">${e('saveDraft')}</button></div></form></section></div>`;
}
async function connections() {
  const [rows, suppliers] = await Promise.all([api('/v1/connections'), api('/v1/providers')]);
  const names = Object.fromEntries(suppliers.map(x => [x.id, x.name]));
  return heading('connections', button('refresh', 'refresh')) + `<div class="panel"><h2>${e('createConnection')}</h2><form id="connection-form"><div class="form-grid">${select('providerId', 'selectProvider', [['', t('choose')], ...suppliers.filter(x => x.enabled).map(x => [x.id, x.name])])}${input('connectionName', 'connectionName', '', 'required maxlength="100"')}${input('credential', 'credential', '', 'type="password" autocomplete="new-password"', 'credentialHint')}</div><div class="form-actions"><button class="button primary" type="submit">+ ${e('createConnection')}</button></div></form></div><div class="panel"><h2>${e('connectionList')}</h2>${rows.length ? `<div class="table-wrap"><table><thead><tr><th>${e('connectionName')}</th><th>${e('providerName')}</th><th>${e('state')}</th><th>${e('actions')}</th></tr></thead><tbody>${rows.map(row => `<tr><td><span class="primary-cell">${escape(row.name)}</span><span class="secondary-cell code">${short(row.id)}</span></td><td>${escape(names[row.providerId] || row.providerId)}</td><td>${badge(row.enabled ? 'enabled' : 'disabled')}</td><td><div class="toolbar">${button('health', 'connection-health', row.id)}${button('rotate', 'rotate-credential', row.id)}${button(row.enabled ? 'disable' : 'enable', 'toggle-connection', row.id, 'small', `data-enabled="${!row.enabled}"`)}</div></td></tr>`).join('')}</tbody></table></div>` : empty('noConnections')}</div>`;
}
function renderTargets(eventType) {
  const targets = state.targets.filter(x => x.eventType === eventType);
  return targets.map(target => `<label class="target-option"><input type="checkbox" name="target" value="${escape(target.connectionId + '|' + target.operation)}"><span><strong>${escape(target.name)}</strong><small>${escape(target.provider)} &middot; ${escape(target.operation)}</small><span class="operation-description">${escape(target.description || t('noDescription'))}</span></span></label>`).join('') || empty('connectFirst');
}
async function send() {
  const [connections, suppliers] = await Promise.all([api('/v1/connections'), api('/v1/providers')]);
  const active = connections.filter(x => x.enabled && suppliers.some(p => p.id === x.providerId && p.enabled));
  const definitions = new Map(await mapLimited([...new Set(active.map(x => x.providerId))], async id => [id, latestPublished(await api(`/v1/providers/${id}/versions`))]));
  state.targets = active.flatMap(c => (definitions.get(c.providerId) || []).map(v => ({ connectionId: c.id, operation: v.operation, eventType: JSON.parse(v.definition).eventType, description: JSON.parse(v.definition).description, name: c.name, provider: suppliers.find(p => p.id === c.providerId)?.name || '' })));
  const events = [...new Set(state.targets.map(x => x.eventType))];
  if (!events.length) return heading('send') + `<div class="panel">${empty('connectFirst', link('createConnection', 'connections', true))}</div>`;
  return heading('send') + `<div class="split compose-layout"><div class="panel"><form id="send-form"><div class="form-grid">${select('eventType', 'eventType', events.map(x => [x, x]), events[0])}${input('expiry', 'expiresAt', '', 'type="datetime-local"', 'expiryHint')}<div class="field full"><label>${e('recipients')}</label><div class="target-list" id="targets">${renderTargets(events[0])}</div></div>${textarea('payload', 'payload', pretty(samplePayload), 'large', 'payloadHint')}<div class="field full"><label for="idempotencyKey">${e('idempotencyKey')}</label><div class="toolbar"><input id="idempotencyKey" name="idempotencyKey" value="${crypto.randomUUID()}" required maxlength="200" style="flex:1">${button('newKey', 'new-key')}</div><small>${e('keyHint')}</small></div></div><div class="form-actions"><button class="button primary" type="submit">${e('submitNotification')} &rarr;</button></div></form><div id="receipt" class="receipt" hidden></div></div><aside class="panel"><h2>${e('submissionTips')}</h2><ol class="help-list"><li>${e('sendTip1')}</li><li>${e('sendTip2')}</li><li>${e('sendTip3')}</li></ol><div class="callout">${e('acceptedHint')}</div></aside></div>`;
}
async function notifications() {
  const rows = await api(`/v1/notifications?page=${state.page}&pageSize=20${state.filter ? '&status=' + encodeURIComponent(state.filter) : ''}`);
  const filters = [['', t('allStates')], ...['pending', 'processing', 'retry_wait', 'succeeded', 'stopped', 'expired', 'cancelled'].map(x => [x, t(x)])];
  return heading('notifications', button('refresh', 'refresh') + link('newNotification', 'send', true)) + `<div class="panel"><div class="panel-header"><h2>${e('notifications')}</h2>${select('status-filter', 'state', filters, state.filter)}</div>${notificationTable(rows.items)}<div class="pagination"><span>${e('total')} ${rows.total} ${e('items')} &middot; ${state.page} ${e('page')}</span>${button('previous', 'previous-page', '', 'small', state.page === 1 ? 'disabled' : '')}${button('next', 'next-page', '', 'small', state.page * 20 >= rows.total ? 'disabled' : '')}</div></div>`;
}
async function notificationDetail(id) {
  const view = await api(`/v1/notifications/${encodeURIComponent(id)}`);
  const [connections, histories] = await Promise.all([api('/v1/connections'), mapLimited(view.deliveries, d => api(`/v1/deliveries/${d.id}/attempts`))]);
  const names = Object.fromEntries(connections.map(c => [c.id, c.name]));
  return `<div class="page-heading"><div><a href="#notifications" class="muted">&larr; ${e('notifications')}</a><h1 style="margin-top:10px">${e('deliveryDetails')}</h1><span class="code">${escape(view.notification.id)}</span></div>${button('refresh', 'refresh')}</div><div class="panel"><h2>${escape(view.notification.eventType)}</h2><div class="detail-grid"><div><small>${e('createdAt')}</small>${date(view.notification.createdAt)}</div><div><small>${e('idempotencyKey')}</small><span class="code">${escape(view.notification.idempotencyKey)}</span></div></div><details><summary>${e('originalPayload')}</summary><pre>${escape(pretty(JSON.parse(view.notification.data)))}</pre></details></div>${view.deliveries.map((d, index) => `<section class="panel"><div class="panel-header"><h2>${escape(names[d.connectionId] || d.connectionId)} &middot; ${escape(d.operation)}</h2>${badge(d.status)}</div><div class="detail-grid"><div><small>${e('deliveryId')}</small><span class="code">${escape(d.id)}</span></div><div><small>${e('expiresAt')}</small>${date(d.expiresAt)}</div><div><small>${e('attemptCount')}</small>${d.attempts}</div><div><small>${e('lastError')}</small>${e(d.lastError === 'accepted' ? 'supplierAccepted' : d.lastError || '-')}</div></div>${d.uncertain ? `<div class="callout warning">${e('uncertain')}</div>` : ''}<h3>${e('attemptHistory')}</h3>${histories[index].length ? `<ol class="timeline">${histories[index].map(a => `<li><strong>${e(a.category === 'accepted' ? 'supplierAccepted' : a.category)}</strong> ${a.httpStatus ? `<span class="badge neutral">HTTP ${a.httpStatus}</span>` : ''}<div class="muted">${date(a.startedAt)}${a.finishedAt ? ' &rarr; ' + date(a.finishedAt) : ''}</div></li>`).join('')}</ol>` : `<p class="muted">${e('noAttempts')}</p>`}<div class="toolbar">${d.status === 'stopped' ? button('retryAction', 'delivery-action', d.id, 'small', 'data-kind="retry"') + button('confirmAction', 'delivery-action', d.id, 'small', 'data-kind="confirm"') : ''}${['pending', 'processing', 'retry_wait', 'stopped'].includes(d.status) ? button('cancelAction', 'delivery-action', d.id, 'small danger', 'data-kind="cancel"') : ''}</div></section>`).join('')}`;
}
function ticketTable(rows) {
  if (!rows.length) return empty();
  return `<div class="table-wrap"><table><thead><tr><th>${e('category')}</th><th>${e('state')}</th><th>${e('affected')}</th><th>${e('actions')}</th></tr></thead><tbody>${rows.map(row => `<tr><td><span class="primary-cell">${e(row.category)}</span><span class="secondary-cell code">${short(row.id)} &middot; ${escape(row.operation)}</span></td><td>${badge(row.status)}</td><td>${row.affectedCount}</td><td>${button('detail', 'ticket-detail', row.id)} ${row.status === 'open' ? button('resolve', 'resolve-ticket', row.id) : ''}</td></tr>`).join('')}</tbody></table></div>`;
}
async function tickets() { return heading('tickets', button('refresh', 'refresh')) + `<div class="panel">${ticketTable(await api('/v1/tickets'))}</div>`; }
async function settings() {
  return heading('settings') + `<div class="panel" style="max-width:800px">${state.config.demo ? `<div class="callout">${e('demoSettings')}</div>` : `<form id="settings-form"><div class="form-grid">${input('businessKey', 'businessKey', '', 'type="password" autocomplete="off"')}${input('providerKey', 'providerKey', '', 'type="password" autocomplete="off"')}</div><p class="muted">${e('settingsHint')}</p><div class="form-actions"><button type="submit" class="button primary">${e('saveSettings')}</button></div></form>`}</div>`;
}
async function loadRoute() {
  const generation = ++state.generation;
  navigation();
  const fullRoute = location.hash.slice(1) || 'overview';
  const name = routeName();
  $('main').innerHTML = `<div class="loading">${e('loading')}</div>`;
  try {
    const html = fullRoute.startsWith('notifications/') ? await notificationDetail(fullRoute.split('/')[1]) : await ({ overview, providers, connections, send, notifications, tickets, settings }[name] || overview)();
    if (generation === state.generation) $('main').innerHTML = html + `<footer class="page-footer">${e('latestResults')} &middot; ${date(new Date().toISOString())}</footer>`;
  } catch (error) {
    if (generation === state.generation) $('main').innerHTML = `<div class="inline-error">${escape(t(error.message))}</div>${link('settings', 'settings')} ${button('refresh', 'refresh')}`;
  }
}

document.addEventListener('click', async event => {
  const element = event.target.closest('button[data-action]');
  if (!element) return;
  const { action, id, kind } = element.dataset;
  element.disabled = true;
  try {
    if (action === 'refresh') await loadRoute();
    else if (action === 'select-provider') { state.selectedProvider = id; await loadRoute(); }
    else if (action === 'previous-page' || action === 'next-page') { state.page += action === 'next-page' ? 1 : -1; await loadRoute(); }
    else if (action === 'toggle-provider') { await api(`/v1/providers/${id}/enabled`, { method: 'PATCH', body: { enabled: element.dataset.enabled === 'true' }, role: 'provider' }); notify('saved'); await loadRoute(); }
    else if (action === 'toggle-connection') { await api(`/v1/connections/${id}/enabled`, { method: 'PATCH', body: { enabled: element.dataset.enabled === 'true' } }); notify('saved'); await loadRoute(); }
    else if (action === 'publish') { await api(`/v1/providers/${state.selectedProvider}/versions/${id}/publish`, { method: 'POST', role: 'provider' }); notify('published'); await loadRoute(); }
    else if (action === 'sample-version') { $('inputs').value = pretty(sampleInputs); $('mapping').value = pretty(sampleMapping); $('successRule').value = pretty(sampleSuccess); notify('sampleLoaded'); }
    else if (action === 'preview') modal('preview', `<p class="muted">${e('previewHint')}</p><form id="preview-form" data-provider="${state.selectedProvider}" data-version="${id}">${textarea('sampleData', 'sampleData', pretty(samplePayload))}<div class="form-actions"><button type="submit" class="button primary">${e('preview')}</button></div></form><pre id="preview-result" hidden></pre>`);
    else if (action === 'new-key') { $('idempotencyKey').value = crypto.randomUUID(); $('receipt').hidden = true; }
    else if (action === 'rotate-credential') modal('rotate', `<form id="credential-form" data-id="${id}">${input('newCredential', 'credential', '', 'type="password" required autocomplete="new-password"')}<div class="form-actions"><button class="button primary" type="submit">${e('save')}</button></div></form>`);
    else if (action === 'connection-health') {
      const rows = await api(`/v1/connections/${id}/health`);
      modal('health', `<p class="muted">${e('healthHint')}</p>${rows.length ? rows.map(h => `<div class="panel"><h3>${escape(h.operation)} ${badge(h.state, 'health')}</h3><div class="detail-grid"><div><small>${e('failureRate')}</small>${h.failureRate == null ? '-' : (h.failureRate * 100).toFixed(1) + '%'}</div><div><small>${e('backlog')}</small>${h.pendingCount}</div><div><small>${e('lastSuccess')}</small>${date(h.lastSuccessAt)}</div></div></div>`).join('') : empty()}`);
    }
    else if (action === 'delivery-action') modal(kind + 'Action', `<div class="callout ${kind === 'cancel' ? 'warning' : ''}">${e(kind + 'Hint')}</div><form id="delivery-action-form" data-id="${id}" data-kind="${kind}"><div class="field"><label for="reason">${e('reason')}</label><textarea id="reason" name="reason" required minlength="3" maxlength="1000"></textarea><small>${e('reasonHint')}</small></div><div class="form-actions"><button type="submit" class="button ${kind === 'cancel' ? 'danger' : 'primary'}">${e('perform')}</button></div></form>`);
    else if (action === 'resolve-ticket') modal('resolve', `<p class="callout warning">${e('resolveHint')}</p><form id="resolve-ticket-form" data-id="${id}"><div class="form-grid">${select('resolution', 'resolution', ['fixed', 'acknowledged', 'no_action'].map(x => [x, t(x)]), 'fixed')}${input('reason', 'reason', '', 'required minlength="3" maxlength="1000"')}</div><div class="form-actions"><button type="submit" class="button primary">${e('resolve')}</button></div></form>`);
    else if (action === 'ticket-detail') { const result = await api(`/v1/tickets/${id}`); modal('ticketId', `<div class="code">${escape(id)}</div><div class="detail-grid"><div><small>${e('category')}</small>${e(result.ticket.category)}</div><div><small>${e('state')}</small>${badge(result.ticket.status)}</div><div><small>${e('affected')}</small>${result.ticket.affectedCount}</div><div><small>${e('updatedAt')}</small>${date(result.ticket.updatedAt)}</div></div><details><summary>${e('detail')}</summary><pre>${escape(pretty(result))}</pre></details>`); }
  } catch (error) { notify(error.message, true); }
  finally { element.disabled = false; }
});
document.addEventListener('change', event => {
  if (event.target.id === 'status-filter') { state.filter = event.target.value; state.page = 1; loadRoute(); }
  if (event.target.id === 'eventType' && $('send-form')) $('targets').innerHTML = renderTargets(event.target.value);
});
document.addEventListener('submit', async event => {
  const form = event.target;
  if (!(form instanceof HTMLFormElement)) return;
  event.preventDefault();
  if (!form.reportValidity()) return;
  const submitButton = form.querySelector('button[type=submit]');
  if (submitButton.disabled) return;
  submitButton.disabled = true;
  const data = Object.fromEntries(new FormData(form));
  try {
    if (form.id === 'register-provider') { const row = await api('/v1/providers', { method: 'POST', body: { name: data.providerName }, role: 'provider' }); state.selectedProvider = row.id; notify('registered'); await loadRoute(); }
    else if (form.id === 'version-form') { await api(`/v1/providers/${form.dataset.provider}/operations/${encodeURIComponent(data.operation)}/versions`, { method: 'POST', body: operationDefinition(data), role: 'provider' }); notify('saved'); await loadRoute(); }
    else if (form.id === 'preview-form') { const result = await api(`/v1/providers/${form.dataset.provider}/versions/${form.dataset.version}/preview`, { method: 'POST', body: { data: parseObject(data.sampleData) }, role: 'provider' }); $('preview-result').textContent = pretty(result); $('preview-result').hidden = false; }
    else if (form.id === 'connection-form') { await api('/v1/connections', { method: 'POST', body: { providerId: data.providerId, name: data.connectionName, credential: data.credential || null } }); notify('connected'); await loadRoute(); }
    else if (form.id === 'credential-form') { await api(`/v1/connections/${form.dataset.id}/credential`, { method: 'PUT', body: { credential: data.newCredential } }); $('dialog').close(); notify('saved'); }
    else if (form.id === 'send-form') {
      const targets = [...form.querySelectorAll('input[name=target]:checked')].map(node => { const [connectionId, operation] = node.value.split('|'); return { connectionId, operation }; });
      const body = submission({ eventType: data.eventType, key: data.idempotencyKey, payload: data.payload, targets, expiry: data.expiry });
      const receipt = await api('/v1/notifications', { method: 'POST', body, key: data.idempotencyKey });
      $('receipt').innerHTML = `<h3>&#10003; ${e('accepted')}</h3><p>${e(receipt.duplicate ? 'duplicateReceipt' : 'acceptedHint')}</p><p class="code">${escape(receipt.id)}</p><div class="toolbar"><a class="button primary" href="#notifications/${receipt.id}">${e('detail')} &rarr;</a></div>`;
      $('receipt').hidden = false; notify('accepted');
    }
    else if (form.id === 'delivery-action-form') { await api(`/v1/deliveries/${form.dataset.id}/${form.dataset.kind}`, { method: 'POST', body: { reason: data.reason } }); $('dialog').close(); notify('actionDone'); await loadRoute(); }
    else if (form.id === 'resolve-ticket-form') { await api(`/v1/tickets/${form.dataset.id}/resolve`, { method: 'POST', body: { resolution: data.resolution, reason: data.reason } }); $('dialog').close(); notify('actionDone'); await loadRoute(); }
    else if (form.id === 'settings-form') { state.keys.business = data.businessKey; state.keys.provider = data.providerKey; form.reset(); notify('saved'); location.hash = '#overview'; }
  } catch (error) { notify(error.message, true); }
  finally { submitButton.disabled = false; }
});
$('close-dialog').onclick = () => $('dialog').close();
$('language').onchange = async () => {
  const previousRoute = location.hash;
  const drafts = [...$('main').querySelectorAll('form')].map(form => ({ id: form.id, fields: [...form.elements].filter(x => x.name).map(x => ({ name: x.name, value: x.value, type: x.type, checked: x.checked })) }));
  state.language = $('language').value; localStorage.setItem('hub.language', state.language);
  state.words = await (await fetch(`/locales/${state.language}.json`)).json();
  document.documentElement.lang = state.language;
  $('dialog').close(); await loadRoute();
  if (location.hash === previousRoute) for (const draft of drafts) {
    const form = $(draft.id); if (!form) continue;
    for (const field of draft.fields.filter(x => x.type !== 'checkbox')) {
      const control = [...form.elements].find(x => x.name === field.name);
      if (control) control.value = field.value;
    }
    if (draft.id === 'send-form') $('targets').innerHTML = renderTargets($('eventType').value);
    for (const field of draft.fields.filter(x => x.type === 'checkbox')) {
      const control = [...form.elements].find(x => x.name === field.name && x.value === field.value);
      if (control) control.checked = field.checked;
    }
  }
};
window.addEventListener('hashchange', () => { $('dialog').close(); loadRoute(); });

try {
  try { const config = await fetch('/local-demo-config'); if (config.ok) state.config = await config.json(); } catch { /* Normal API hosting does not expose demo configuration. */ }
  if (!['en', 'zh-CN'].includes(state.language)) state.language = 'en';
  state.words = await (await fetch(`/locales/${state.language}.json`)).json();
  $('language').value = state.language; document.documentElement.lang = state.language;
  await loadRoute();
  setInterval(() => { if (['overview', 'notifications'].includes(routeName()) && !$('dialog').open && document.visibilityState === 'visible') loadRoute(); }, 15000);
} catch (error) { $('main').textContent = 'Unable to load the console: ' + error.message; }
