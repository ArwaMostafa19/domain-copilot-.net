import test from "node:test";
import assert from "node:assert/strict";
import { ApiClient, ApiError, parseJsonEvent, readSse, responseErrorMessage } from "../../src/DomainCopilot.Api/static/frontend-api.js";

test("authenticated requests preserve JSON contracts and bearer identity", async () => {
  let captured;
  const client = new ApiClient({ fetchImpl: async (url, options) => { captured={url,options}; return new Response('{"ok":true}'); } });
  client.token="test-token";
  assert.deepEqual(await client.json("/runs",{method:"POST",body:{symptoms:"pump",installed_revision:null}}),{ok:true});
  assert.equal(captured.url,"/runs");
  assert.equal(captured.options.headers.get("Authorization"),"Bearer test-token");
  assert.deepEqual(JSON.parse(captured.options.body),{symptoms:"pump",installed_revision:null});
});
test("login omits the bearer token and parses both .NET error envelopes", async () => {
  let captured;
  const client=new ApiClient({fetchImpl:async(_url,options)=>{captured=options;return new Response('{"error":"invalid credentials","correlation_id":"safe-id"}',{status:401})}});
  client.token="must-not-send";
  await assert.rejects(client.request("/auth/login",{method:"POST",authenticated:false,body:{tenant_id:"tenant-a"}}),e=>e instanceof ApiError&&e.status===401&&e.message==="invalid credentials"&&e.correlationId==="safe-id");
  assert.equal(captured.headers.has("Authorization"),false);
  assert.equal(responseErrorMessage({detail:[{loc:["body","question"],msg:"Field required"}]},422),"body.question: Field required");
});
test("authenticated 401 invokes session expiration callback",async()=>{
  let expired=0;const client=new ApiClient({onUnauthorized:()=>expired++,fetchImpl:async()=>new Response('{"detail":"expired"}',{status:401})});client.token="expired";
  await assert.rejects(client.json("/me"),e=>e.status===401);assert.equal(expired,1);
});
test("SSE parser handles chunks, CRLF, comments, multiline data, and boundaries",async()=>{
  const bytes=new TextEncoder().encode(': ping\r\nevent: update\r\ndata: {\r\ndata: "x": 1,\r\ndata: "emoji": "🙂"}\r\n\r\nid: final\ndata: [DONE]\n\n');
  const chunks=[bytes.slice(0,7),bytes.slice(7,19),bytes.slice(19,43),bytes.slice(43)];
  const stream=new ReadableStream({start(c){chunks.forEach(x=>c.enqueue(x));c.close()}});
  const frames=[];for await(const frame of readSse(stream))frames.push(frame);
  assert.equal(frames.length,2);assert.equal(frames[0].event,"update");
  assert.deepEqual(parseJsonEvent(frames[0]),{x:1,emoji:String.fromCodePoint(0x1f642)});
  assert.equal(frames[1].id,"final");assert.equal(frames[1].data,"[DONE]");
});
test("SSE parser dispatches a final unterminated frame and ignores malformed JSON",async()=>{
  const stream=new ReadableStream({start(c){c.enqueue(new TextEncoder().encode("data: {bad"));c.close()}});
  const frames=[];for await(const frame of readSse(stream))frames.push(frame);
  assert.equal(frames[0].data,"{bad");assert.equal(parseJsonEvent(frames[0]),null);
});
test("SSE parser cancels blocked reads and releases its reader on abort",async()=>{
  let cancelled=false;const controller=new AbortController();
  const stream=new ReadableStream({pull(){},cancel(){cancelled=true}});
  const iterator=readSse(stream,{signal:controller.signal})[Symbol.asyncIterator]();
  const pending=iterator.next();controller.abort();
  await assert.rejects(pending,e=>e.name==="AbortError");
  assert.equal(cancelled,true);
});
