// 浏览器端：目录浏览 + 下载 + 分块上传（支持断点续传）

const CHUNK_SIZE = 4 * 1024 * 1024; // 与服务端约定：64KB~32MB
const CHUNK_CONCURRENCY = 3;
const MAX_CHUNK_RETRY = 3;

const state = {
  path: '',
  entries: [],
  info: { allowUpload: true, rootName: '' },
  selectMode: false,
  selected: new Set(), // rel paths
  sort: 'name_asc',
};

const $ = (id) => document.getElementById(id);
const crumbs = $('crumbs'), list = $('list'), empty = $('empty');
const searchBox = $('search'), selectBtn = $('selectBtn'), uploadBtn = $('uploadBtn'), fileInput = $('fileInput');
const selBar = $('selBar'), selCount = $('selCount'), zipBtn = $('zipBtn'), clearBtn = $('clearBtn');
const dropOverlay = $('dropOverlay');
const uploadPanel = $('uploadPanel'), uploadList = $('uploadList'), clearUploadsBtn = $('clearUploads');

// ---------------- 工具 ----------------

function humanBytes(n) {
  if (!Number.isFinite(n) || n < 0) return '';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let v = n, u = 0;
  while (v >= 1024 && u < units.length - 1) { v /= 1024; u++; }
  return (u === 0 ? v : v.toFixed(v >= 100 ? 0 : 1)) + ' ' + units[u];
}

function humanTime(ms) {
  if (!ms) return '';
  const d = new Date(ms);
  const pad = (x) => String(x).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function joinRel(dir, name) {
  return dir ? dir + '/' + name : name;
}

function fileUrl(rel) {
  return '/files/' + rel.split('/').map(encodeURIComponent).join('/');
}

function iconOf(name, isDir) {
  if (isDir) return '📁';
  const ext = name.includes('.') ? name.split('.').pop().toLowerCase() : '';
  if (['jpg','jpeg','png','gif','webp','bmp','svg','ico','heic'].includes(ext)) return '🖼️';
  if (['mp4','mkv','avi','mov','wmv','flv','m4v','webm','ts','rmvb'].includes(ext)) return '🎬';
  if (['mp3','flac','wav','aac','ogg','m4a','ape','wma'].includes(ext)) return '🎵';
  if (['zip','rar','7z','tar','gz','bz2','xz','iso'].includes(ext)) return '📦';
  if (ext === 'pdf') return '📕';
  if (['doc','docx','wps','rtf'].includes(ext)) return '📘';
  if (['xls','xlsx','csv'].includes(ext)) return '📗';
  if (['ppt','pptx'].includes(ext)) return '📙';
  if (['exe','msi','bat','cmd','apk'].includes(ext)) return '⚙️';
  if (['c','cpp','h','cs','py','java','js','ts','go','rs','html','css','sql','sh'].includes(ext)) return '🧩';
  return '📄';
}

async function api(url, body) {
  const resp = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body || {}),
  });
  const data = await resp.json().catch(() => ({}));
  if (!resp.ok) throw new Error(data.error || `请求失败 (${resp.status})`);
  return data;
}

// ---------------- 目录浏览 ----------------

async function load(path) {
  try {
    const resp = await fetch('/api/list?path=' + encodeURIComponent(path));
    const data = await resp.json();
    if (!resp.ok) throw new Error(data.error || '加载失败');
    state.path = data.path || '';
    state.entries = data.entries || [];
    state.info.rootName = data.rootName || '';
    state.selected.clear();
    render();
  } catch (e) {
    list.innerHTML = '';
    empty.hidden = false;
    empty.textContent = '加载失败：' + e.message;
  }
}

function sortedEntries() {
  const arr = [...state.entries];
  const dirFirst = (a, b) => (a.isDir === b.isDir ? 0 : a.isDir ? -1 : 1);
  const [key, dir] = {
    name_asc: ['name', 1],
    name_desc: ['name', -1],
    size_desc: ['size', -1],
    time_desc: ['modified', -1],
  }[state.sort];
  const coll = new Intl.Collator('zh-Hans-CN', { numeric: true, sensitivity: 'base' });
  arr.sort((a, b) => dirFirst(a, b) || dir * coll.compare(a[key], b[key]));
  return arr;
}

function filteredEntries() {
  const q = searchBox.value.trim().toLowerCase();
  return q ? sortedEntries().filter((e) => e.name.toLowerCase().includes(q)) : sortedEntries();
}

function render() {
  renderCrumbs();
  renderList();
  updateSelBar();
}

function renderCrumbs() {
  crumbs.innerHTML = '';
  const root = document.createElement('a');
  root.className = 'crumb';
  root.textContent = '📦 ' + (state.info.rootName || '共享');
  root.onclick = () => load('');
  crumbs.appendChild(root);
  const parts = state.path ? state.path.split('/') : [];
  let acc = '';
  for (const part of parts) {
    acc = acc ? acc + '/' + part : part;
    const rel = acc;
    const sep = document.createElement('span');
    sep.className = 'sep';
    sep.textContent = '/';
    const a = document.createElement('a');
    a.className = 'crumb';
    a.textContent = part;
    a.onclick = () => load(rel);
    crumbs.appendChild(sep);
    crumbs.appendChild(a);
  }
}

function renderList() {
  const entries = filteredEntries();
  list.innerHTML = '';
  empty.hidden = entries.length > 0;
  empty.textContent = '此文件夹为空';

  for (const entry of entries) {
    const rel = joinRel(state.path, entry.name);
    const row = document.createElement('div');
    row.className = 'row' + (state.selected.has(rel) ? ' selected' : '');
    row.role = 'listitem';

    if (state.selectMode) {
      const cb = document.createElement('input');
      cb.type = 'checkbox';
      cb.checked = state.selected.has(rel);
      cb.onchange = () => { cb.checked ? state.selected.add(rel) : state.selected.delete(rel); render(); };
      row.appendChild(cb);
    }

    const icon = document.createElement('span');
    icon.className = 'icon';
    icon.textContent = iconOf(entry.name, entry.isDir);
    row.appendChild(icon);

    const name = document.createElement('span');
    name.className = 'name';
    name.textContent = entry.name;
    name.title = entry.name;
    row.appendChild(name);

    if (!entry.isDir) {
      const size = document.createElement('span');
      size.className = 'meta size';
      size.textContent = humanBytes(entry.size);
      row.appendChild(size);
    }

    const time = document.createElement('span');
    time.className = 'meta time';
    time.textContent = humanTime(entry.modified);
    row.appendChild(time);

    if (!entry.isDir) {
      const dl = document.createElement('a');
      dl.className = 'dl';
      dl.href = fileUrl(rel);
      dl.download = entry.name;
      dl.title = '下载';
      dl.textContent = '⬇';
      dl.onclick = (e) => e.stopPropagation();
      row.appendChild(dl);
    }

    row.onclick = () => {
      if (state.selectMode) {
        state.selected.has(rel) ? state.selected.delete(rel) : state.selected.add(rel);
        render();
      } else if (entry.isDir) {
        load(rel);
      } else {
        window.open(fileUrl(rel), '_blank');
      }
    };
    list.appendChild(row);
  }
}

// ---------------- 多选打包 ----------------

function updateSelBar() {
  selBar.hidden = !state.selectMode;
  selCount.textContent = state.selected.size;
}

selectBtn.onclick = () => {
  state.selectMode = !state.selectMode;
  selectBtn.classList.toggle('primary', state.selectMode);
  if (!state.selectMode) state.selected.clear();
  render();
};

clearBtn.onclick = () => { state.selected.clear(); render(); };

zipBtn.onclick = () => {
  if (state.selected.size === 0) return;
  const qs = [...state.selected].map((p) => 'path=' + encodeURIComponent(p)).join('&');
  const a = document.createElement('a');
  a.href = '/zip?' + qs;
  a.download = '';
  document.body.appendChild(a);
  a.click();
  a.remove();
};

// ---------------- 上传 ----------------

const tasks = []; // {file, rel, status, received, total, uploadId, ctrl, refs}
let pumping = false;

if (state.info.allowUpload === false) uploadBtn.parentElement.hidden = true;

uploadBtn.onclick = () => fileInput.click();
fileInput.onchange = () => {
  enqueueFiles([...fileInput.files].map((f) => ({ file: f, rel: '' })));
  fileInput.value = '';
};

clearUploadsBtn.onclick = () => {
  for (let i = tasks.length - 1; i >= 0; i--) {
    const t = tasks[i];
    if (['done', 'error', 'canceled'].includes(t.status)) {
      t.refs?.root?.remove();
      tasks.splice(i, 1);
    }
  }
  if (!tasks.length) uploadPanel.hidden = true;
};

function enqueueFiles(items) {
  if (!items.length) return;
  // 上传到发起时的当前目录；拖入文件夹时保留其子路径前缀
  const baseDir = state.path;
  for (const { file, rel } of items) {
    tasks.push({
      file,
      rel: baseDir ? (rel ? baseDir + '/' + rel : baseDir) : rel,
      status: 'queued',
      received: 0,
      total: file.size,
      uploadId: null,
      ctrl: null,
      refs: null,
    });
  }
  uploadPanel.hidden = false;
  renderAllTasks();
  pump();
}

function taskAction(t) {
  if (t.status === 'uploading' || t.status === 'queued') {
    // 暂停
    t.ctrl?.abort();
    if (t.status === 'queued') { t.status = 'paused'; renderTask(t); }
  } else if (t.status === 'paused' || t.status === 'error') {
    t.status = 'queued';
    renderTask(t);
    pump();
  }
}

async function pump() {
  if (pumping) return;
  pumping = true;
  try {
    while (true) {
      const t = tasks.find((x) => x.status === 'queued');
      if (!t) break;
      t.status = 'uploading';
      renderTask(t);
      try {
        await runUpload(t);
        t.status = 'done';
        t.received = t.total;
        renderTask(t);
      } catch (e) {
        if (e.name === 'AbortError' || t.status !== 'uploading') {
          t.status = 'paused';
        } else {
          t.status = 'error';
          t.error = e.message;
        }
        renderTask(t);
      }
    }
  } finally {
    pumping = false;
  }
}

async function runUpload(t) {
  const init = await api('/api/upload/init', {
    name: t.file.name,
    path: t.rel,
    size: t.file.size,
    chunkSize: CHUNK_SIZE,
    lastModified: Math.floor(t.file.lastModified || 0),
  });
  t.uploadId = init.uploadId;
  const receivedSet = new Set(init.received || []);
  const count = Math.max(1, Math.ceil(t.file.size / CHUNK_SIZE));
  t.received = [...receivedSet].reduce((s, i) => s + chunkLen(t.file.size, i), 0);
  t.samples = [{ t: Date.now(), bytes: t.received }];
  renderTask(t);

  let nextIdx = 0;
  let inFlight = 0;
  t.ctrl = new AbortController();

  await new Promise((resolve, reject) => {
    let settled = false;
    const fail = (err) => { if (!settled) { settled = true; reject(err); } };
    const done = () => { if (!settled && nextIdx >= count && inFlight === 0) { settled = true; resolve(); } };

    const startNext = () => {
      if (settled) return;
      while (inFlight < CHUNK_CONCURRENCY && nextIdx < count) {
        const i = nextIdx++;
        if (receivedSet.has(i)) continue;
        inFlight++;
        putChunk(t, i)
          .then((len) => {
            receivedSet.add(i);
            t.received += len;
            updateSpeed(t);
            renderTask(t);
          })
          .catch(fail)
          .finally(() => { inFlight--; startNext(); done(); });
      }
      done();
    };
    startNext();
  });

  await api('/api/upload/complete', { uploadId: t.uploadId });
}

function chunkLen(size, index) {
  const start = index * CHUNK_SIZE;
  return Math.min(CHUNK_SIZE, size - start);
}

async function putChunk(t, index) {
  const start = index * CHUNK_SIZE;
  const blob = t.file.slice(start, start + CHUNK_SIZE);
  let lastErr;
  for (let attempt = 1; attempt <= MAX_CHUNK_RETRY; attempt++) {
    try {
      const resp = await fetch(`/api/upload/chunk/${t.uploadId}/${index}`, {
        method: 'PUT',
        body: blob,
        signal: t.ctrl.signal,
      });
      const data = await resp.json().catch(() => ({}));
      if (!resp.ok || !data.ok) throw new Error(data.error || `分块上传失败 (${resp.status})`);
      return data.received ?? blob.size;
    } catch (e) {
      if (e.name === 'AbortError') throw e;
      lastErr = e;
      if (attempt < MAX_CHUNK_RETRY) await new Promise((r) => setTimeout(r, 800 * attempt));
    }
  }
  throw lastErr;
}

function updateSpeed(t) {
  t.samples = t.samples || [];
  t.samples.push({ t: Date.now(), bytes: t.received });
  while (t.samples.length > 2 && Date.now() - t.samples[0].t > 6000) t.samples.shift();
}

function taskSpeed(t) {
  const s = t.samples || [];
  if (s.length < 2) return '';
  const first = s[0], last = s[s.length - 1];
  const dt = (last.t - first.t) / 1000;
  if (dt < 0.5 || last.bytes <= first.bytes) return '';
  return humanBytes((last.bytes - first.bytes) / dt) + '/s';
}

const statusText = {
  queued: '等待中',
  uploading: '上传中',
  paused: '已暂停',
  done: '已完成',
  error: '失败',
  canceled: '已取消',
};

function renderAllTasks() {
  for (const t of tasks) renderTask(t);
}

function renderTask(t) {
  if (!t.refs) {
    const root = document.createElement('div');
    root.className = 'task';
    const line = document.createElement('div');
    line.className = 't-line';
    const name = document.createElement('span');
    name.className = 't-name';
    const pct = document.createElement('span');
    pct.className = 't-pct';
    const btn = document.createElement('button');
    btn.className = 't-btn';
    line.append(name, pct, btn);
    const bar = document.createElement('div');
    bar.className = 'bar';
    bar.appendChild(document.createElement('div'));
    const sub = document.createElement('div');
    sub.className = 't-sub';
    root.append(line, bar, sub);
    uploadList.appendChild(root);
    t.refs = { root, name, pct, btn, bar: bar.firstChild, sub };
    btn.onclick = () => {
      if (t.status === 'done' || t.status === 'error' || t.status === 'canceled') {
        t.refs.root.remove();
        tasks.splice(tasks.indexOf(t), 1);
        if (!tasks.length) uploadPanel.hidden = true;
        return;
      }
      if (t.status === 'uploading' || t.status === 'queued') {
        t.wantPause = true;
        t.ctrl?.abort();
        if (t.status === 'queued') { t.status = 'paused'; renderTask(t); }
      } else if (t.status === 'paused') {
        t.status = 'queued';
        renderTask(t);
        pump();
      }
    };
  }

  const r = t.refs;
  r.root.className = 'task ' + (t.status === 'done' ? 'done' : t.status === 'error' ? 'error' : '');
  r.name.textContent = (t.rel ? t.rel + '/' : '') + t.file.name;
  r.name.title = r.name.textContent;
  r.pct.textContent = t.status === 'done' ? '100%' : t.total > 0 ? Math.floor((t.received / t.total) * 100) + '%' : '';
  r.btn.textContent = t.status === 'uploading' || t.status === 'queued' ? '暂停'
    : t.status === 'paused' ? '继续'
    : t.status === 'error' ? '重试' : '✕';
  r.bar.style.width = (t.total > 0 ? (t.received / t.total) * 100 : 0) + '%';
  r.sub.textContent = t.status === 'error' ? (t.error || '上传失败')
    : t.status === 'done' ? `${humanBytes(t.total)} ✔`
    : [humanBytes(t.received), '/', humanBytes(t.total), ' · ', taskSpeed(t)].join('');
}

// 取消仍在队列中的任务
function cancelTask(t) {
  t.ctrl?.abort();
  t.status = 'canceled';
  if (t.uploadId) fetch('/api/upload/' + t.uploadId, { method: 'DELETE' }).catch(() => {});
  renderTask(t);
}

// ---------------- 拖拽上传 ----------------

let dragDepth = 0;

window.addEventListener('dragenter', (e) => {
  if (!hasFiles(e)) return;
  e.preventDefault();
  dragDepth++;
  dropOverlay.hidden = false;
});
window.addEventListener('dragover', (e) => {
  if (hasFiles(e)) e.preventDefault();
});
window.addEventListener('dragleave', (e) => {
  if (--dragDepth <= 0) { dragDepth = 0; dropOverlay.hidden = true; }
});
window.addEventListener('drop', (e) => {
  if (!hasFiles(e)) return;
  e.preventDefault();
  dragDepth = 0;
  dropOverlay.hidden = true;
  collectDropped(e.dataTransfer).then(enqueueFiles).catch(console.error);
});

function hasFiles(e) {
  return e.dataTransfer && [...(e.dataTransfer.types || [])].includes('Files');
}

// 递归收集拖入的文件（支持拖入整个文件夹）
async function collectDropped(dt) {
  const out = [];
  const items = [...(dt.items || [])];
  const entries = items
    .map((it) => (it.webkitGetAsEntry ? it.webkitGetAsEntry() : null))
    .filter(Boolean);

  if (entries.length) {
    await Promise.all(entries.map((entry) => walkEntry(entry, '', out)));
    if (out.length) return out;
  }
  for (const file of dt.files || []) out.push({ file, rel: '' });
  return out;

  async function walkEntry(entry, prefix, out) {
    if (entry.isFile) {
      const file = await new Promise((res, rej) => entry.file(res, rej));
      out.push({ file, rel: prefix });
    } else if (entry.isDirectory) {
      const reader = entry.createReader();
      let batch;
      do {
        batch = await new Promise((res, rej) => reader.readEntries(res, rej));
        for (const child of batch) await walkEntry(child, prefix + entry.name + '/', out);
      } while (batch.length > 0);
    }
  }
}

// ---------------- 排序 / 搜索 ----------------

const sortSelect = document.createElement('select');
sortSelect.innerHTML = `
  <option value="name_asc">名称 ↑</option>
  <option value="name_desc">名称 ↓</option>
  <option value="size_desc">大小 ↓</option>
  <option value="time_desc">时间 ↓</option>`;
sortSelect.style.cssText = 'border:1px solid var(--line);border-radius:8px;padding:7px 6px;font-size:14px;background:var(--bg)';
sortSelect.onchange = () => { state.sort = sortSelect.value; renderList(); };
document.querySelector('.tools').prepend(sortSelect);

let searchTimer;
searchBox.addEventListener('input', () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(renderList, 120);
});

// ---------------- 启动 ----------------

fetch('/api/info').then((r) => r.json()).then((info) => {
  state.info = info;
  if (info.allowUpload === false) {
    uploadBtn.hidden = true;
    fileInput.hidden = true;
  }
}).catch(() => {});

load('');
