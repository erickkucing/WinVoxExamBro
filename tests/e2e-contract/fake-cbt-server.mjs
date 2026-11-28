import http from 'node:http';

const PORT = Number(process.env.FAKE_CBT_PORT || 18089);

let state = {
  violations: [],
  answers: []
};

function readBody(req) {
  return new Promise((resolve) => {
    const chunks = [];
    req.on('data', (chunk) => chunks.push(chunk));
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
  });
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url || '/', `http://127.0.0.1:${PORT}`);

  if (req.method === 'GET' && url.pathname === '/health') {
    res.writeHead(200, { 'Content-Type': 'text/plain' });
    res.end('ok');
    return;
  }

  if (req.method === 'POST' && url.pathname === '/_test/reset') {
    state = { violations: [], answers: [] };
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ ok: true }));
    return;
  }

  if (req.method === 'GET' && url.pathname === '/_test/state') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify(state));
    return;
  }

  if (req.method === 'GET' && url.pathname.startsWith('/exam/workspace/')) {
    const sessionUuid = url.pathname.split('/').pop();
    const html = `<!doctype html>
<html lang="id">
<head>
  <meta charset="utf-8" />
  <title>CBT Workspace Contract Mock</title>
</head>
<body>
  <div id="cbt-anti-cheat-root"
       data-session-uuid="${sessionUuid}"
       data-max-strikes="3"
       data-is-exambrowser="0">
    <div id="anti-cheat-fullscreen-gate" class="flex" style="display:flex">Fullscreen Required</div>
    <textarea id="essay-input"></textarea>
  </div>
</body>
</html>`;
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
    res.end(html);
    return;
  }

  if (req.method === 'POST' && url.pathname.includes('/violation')) {
    const raw = await readBody(req);
    let parsed = raw;
    try {
      parsed = JSON.parse(raw);
    } catch {}
    state.violations.push({ path: url.pathname, body: parsed });
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ action: 'recorded', strikes: state.violations.length, is_locked: false }));
    return;
  }

  if (req.method === 'POST' && url.pathname.includes('/answer')) {
    const raw = await readBody(req);
    let parsed = raw;
    try {
      parsed = JSON.parse(raw);
    } catch {}
    state.answers.push({ path: url.pathname, body: parsed });
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ ok: true, saved: true }));
    return;
  }

  res.writeHead(404, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({ error: 'not_found' }));
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`Fake CBT contract server listening on http://127.0.0.1:${PORT}`);
});
