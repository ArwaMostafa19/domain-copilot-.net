import test from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

const baseUrl = process.env.PHASE5B_BROWSER_TEST_BASE_URL;
const username = process.env.PHASE5B_BROWSER_TEST_USERNAME;
const password = process.env.PHASE5B_BROWSER_TEST_PASSWORD;
const expectedRole = process.env.PHASE5B_BROWSER_TEST_ROLE ?? "technician";
const loginOnly = process.env.PHASE5B_BROWSER_TEST_LOGIN_ONLY === "1";
const browserPath = process.env.PHASE5B_BROWSER_PATH ??
  "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";

test(loginOnly ? "real browser can load the .NET UI and authenticate the existing user" : "real browser can load the .NET UI, authenticate, browse documents, and render ask results", {
  skip: !(baseUrl && username && password)
}, async () => {
  const profile = await mkdtemp(join(tmpdir(), "domaincopilot-browser-"));
  const browser = spawn(browserPath, [
    "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
    "--no-sandbox", "--no-proxy-server", "--remote-allow-origins=*", "--remote-debugging-port=0",
    `--user-data-dir=${profile}`, "about:blank"
  ], { stdio: "ignore", windowsHide: true });
  let socket;
  let nextId = 0;
  const pending = new Map();
  const exceptions = [];
  const responses = [];
  const rpc = (method, params = {}) => new Promise((resolve, reject) => {
    const id = ++nextId;
    pending.set(id, { resolve, reject });
    socket.send(JSON.stringify({ id, method, params }));
  });
  const evaluate = async expression => {
    const response = await rpc("Runtime.evaluate", {
      expression, awaitPromise: true, returnByValue: true, userGesture: true
    });
    if (response.exceptionDetails) throw new Error(response.exceptionDetails.text || "Browser script failed.");
    return response.result?.value;
  };
  const waitFor = async (expression, timeoutMs = 15000) => {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
      const value = await evaluate(expression);
      if (value) return value;
      await new Promise(resolve => setTimeout(resolve, 150));
    }
    const diagnostic = await evaluate("({title:document.title, user:document.querySelector('#user-info-text')?.textContent, loginError:document.querySelector('#login-error')?.textContent, ready:document.readyState, fetchCalls:window.__fetchCalls||[]})");
    throw new Error(`Timed out waiting for browser state ${expression}. Diagnostics: ${JSON.stringify(diagnostic)}; responses: ${JSON.stringify(responses.map(x=>({url:x.url,status:x.status})))}; JavaScript exceptions: ${exceptions.join("; ")}`);
  };

  try {
    let activePort;
    for (let attempt = 0; attempt < 100; attempt++) {
      if (browser.exitCode !== null) throw new Error(`Browser exited with ${browser.exitCode}.`);
      try {
        const active = await readFile(join(profile, "DevToolsActivePort"), "utf8");
        activePort = active.split(/\r?\n/)[0];
        if (activePort) break;
      } catch { /* browser is still starting */ }
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    assert.ok(activePort, "Browser did not expose its private DevTools port.");
    const targets = await (await fetch(`http://127.0.0.1:${activePort}/json/list`)).json();
    const page = targets.find(target => target.type === "page");
    assert.ok(page?.webSocketDebuggerUrl, "Browser page target was not created.");
    socket = new WebSocket(page.webSocketDebuggerUrl);
    await new Promise((resolve, reject) => {
      socket.addEventListener("open", resolve, { once: true });
      socket.addEventListener("error", reject, { once: true });
    });
    socket.addEventListener("message", event => {
      const message = JSON.parse(event.data);
      if (message.method === "Runtime.exceptionThrown") exceptions.push(message.params.exceptionDetails.text);
      if (message.method === "Network.requestWillBeSent") responses.push({ url: message.params.request.url, method: message.params.request.method });
      if (message.method === "Network.loadingFailed") exceptions.push(`Network failure: ${message.params.errorText}`);
      if (message.method === "Network.responseReceived") responses.push({ url: message.params.response.url, status: message.params.response.status });
      if (!message.id) return;
      const operation = pending.get(message.id);
      if (!operation) return;
      pending.delete(message.id);
      if (message.error) operation.reject(new Error(message.error.message));
      else operation.resolve(message.result);
    });
    await rpc("Runtime.enable");
    await rpc("Network.enable");
    await rpc("Page.enable");
    await rpc("Page.addScriptToEvaluateOnNewDocument", { source: `window.__fetchCalls=[];const __realFetch=window.fetch.bind(window);window.fetch=async(...args)=>{const[input,init={}]=args;const parsed=new URL(String(input),location.href);const method=(init.method||(input instanceof Request?input.method:"GET")).toUpperCase();const call={url:parsed.pathname,origin:parsed.origin,method};if(parsed.pathname==="/auth/login"){let payload={};try{payload=JSON.parse(init.body||"{}")}catch{}const headers=new Headers(init.headers||(input instanceof Request?input.headers:undefined));call.tenant_id=payload.tenant_id??null;call.username=payload.username??null;call.passwordPresent=typeof payload.password==="string"&&payload.password.length>0;call.contentType=headers.get("Content-Type")}try{const response=await __realFetch(...args);call.status=response.status;if(parsed.pathname==="/auth/login"){try{call.responseFields=Object.keys(await response.clone().json())}catch{call.responseJson=false}}window.__fetchCalls.push(call);return response}catch(error){call.fetchError=error?.name||"Error";window.__fetchCalls.push(call);throw error}};` });
    await rpc("Page.navigate", { url: baseUrl });
    await waitFor("document.readyState === 'complete' && !!document.querySelector('#login-form')");
    await new Promise(resolve => setTimeout(resolve, 350));

    const credentials = JSON.stringify({ username, password });
    await evaluate(`(() => { const c=${credentials}; document.querySelector('#tenant-select').value='tenant-alpha'; document.querySelector('#username-input').value=c.username; document.querySelector('#password-input').value=c.password; document.querySelector('#login-form').dispatchEvent(new Event('submit',{bubbles:true,cancelable:true})); return true; })()`);
    const identity = await waitFor(`document.querySelector('#user-info-text').textContent.includes(${JSON.stringify(username)})`, 20000);
    assert.ok(identity);
    const session = await evaluate("Boolean(sessionStorage.getItem('domain-copilot-session-token'))");
    assert.equal(session, true, "Bearer session token was not retained for the current browser session.");
    assert.equal(await evaluate("document.body.innerText.includes(sessionStorage.getItem('domain-copilot-session-token'))"), false,
      "Bearer token was exposed in visible page text.");
    assert.equal(await evaluate("document.querySelector('.supervisor-only').hidden"), expectedRole !== "supervisor",
      "Supervisor-only action visibility did not match the authenticated role.");
    const fetchCalls = await evaluate("window.__fetchCalls");
    const loginCall = fetchCalls.find(call => call.url === "/auth/login");
    assert.ok(loginCall, "Frontend did not send POST /auth/login.");
    assert.equal(loginCall.method, "POST");
    assert.equal(loginCall.origin, new URL(baseUrl).origin);
    assert.equal(loginCall.tenant_id, "tenant-alpha");
    assert.equal(loginCall.username, username);
    assert.equal(loginCall.passwordPresent, true, "Frontend omitted the password field.");
    assert.equal(loginCall.contentType, "application/json");
    assert.equal(loginCall.status, 200, "Login API did not return HTTP 200.");
    assert.ok(loginCall.responseFields?.includes("access_token"), "Login response omitted access_token.");
    const meCall = fetchCalls.find(call => call.url === "/me");
    assert.equal(meCall?.status, 200, "Authenticated /me request did not succeed.");
    if (loginOnly) {
      await evaluate("document.querySelector('[data-tab=documents-tab]').click()");
      await waitFor("window.__fetchCalls.some(call => call.url === '/documents' && Number.isInteger(call.status))");
      assert.equal(await evaluate("document.querySelector('#documents-tab').classList.contains('active')"), true);
      assert.equal((await evaluate("window.__fetchCalls.find(call => call.url === '/documents').status")), 200);
      await evaluate("document.querySelector('[data-tab=ask-tab]').click()");
      await waitFor("document.querySelector('#ask-tab').classList.contains('active') && window.__fetchCalls.filter(call => call.url === '/usage' && Number.isInteger(call.status)).length >= 2");
      assert.equal((await evaluate("window.__fetchCalls.filter(call => call.url === '/usage').slice(-1)[0].status")), 200);
      assert.deepEqual(exceptions, [], `Browser JavaScript exception: ${exceptions.join("; ")}`);
      return;
    }

    await evaluate("document.querySelector('[data-tab=documents-tab]').click()");
    await waitFor("document.querySelector('#docs-tbody button')");
    await evaluate("document.querySelector('#docs-tbody button')?.click()");
    await waitFor("document.querySelector('#document-detail').textContent.length > 0");

    await evaluate("document.querySelector('[data-tab=ask-tab]').click()");
    await evaluate("document.querySelector('#ask-input').value='What zero-energy verification must be completed for the ALPHA-HP-200 hydraulic press before opening a guard?'");
    await evaluate("document.querySelector('#ask-submit-btn').click()");
    const answer = await waitFor("document.querySelector('#ask-output').classList.contains('answer-card')", 30000);
    assert.ok(answer);
    await rpc("Emulation.setDeviceMetricsOverride", { width: 390, height: 844, deviceScaleFactor: 1, mobile: true });
    assert.equal(await evaluate("document.documentElement.scrollWidth <= window.innerWidth"), true,
      "The mobile layout should not cause page-level horizontal scrolling.");
    assert.deepEqual(exceptions, [], `Browser JavaScript exception: ${exceptions.join("; ")}`);
  } finally {
    try { socket?.close(); } catch { /* already closed */ }
    if (browser.exitCode === null) {
      browser.kill();
      await new Promise(resolve => browser.once("exit", resolve));
    }
    await rm(profile, { recursive: true, force: true });
  }
});