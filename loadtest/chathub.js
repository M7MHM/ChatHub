import http from 'k6/http';
import { check, fail } from 'k6';
import exec from 'k6/execution';
import { Counter, Trend } from 'k6/metrics';
import { WebSocket } from 'k6/websockets';
import { clearInterval, setInterval, setTimeout } from 'k6/timers';

const env = (name, fallback) => (__ENV[name] === undefined || __ENV[name] === '' ? fallback : __ENV[name]);
const num = (name, fallback) => Number(env(name, fallback));

const BASE_URL = env('BASE_URL', 'http://nginx:8080');
const WS_URL = BASE_URL.replace(/^http/, 'ws');
const SCENARIO = env('SCENARIO', 'read-heavy');
const RESULT_NAME = env('RESULT_NAME', SCENARIO);
const USERS = num('USERS', 200);
const SEED_USERS = num('SEED_USERS', 1000);
const PASSWORD = env('SEED_PASSWORD', 'Seed12345');

const RATE = num('RATE', 30);
const RAMP_UP = num('RAMP_UP', 30);
const WARMUP = num('WARMUP', 30);
const STEADY = num('STEADY', 120);
const RAMP_DOWN = num('RAMP_DOWN', 15);
const WRITE_RATIO = num('WRITE_RATIO', 0.1);

const LISTENERS = num('LISTENERS', 50);
const RT_RATE = num('RT_RATE', 10);
const RT_DURATION = num('RT_DURATION', 120);
const RT_DELAY = 15;

const P95 = {
    me: num('P95_ME', 100),
    conversations: num('P95_CONVERSATIONS', 200),
    messages: num('P95_MESSAGES', 150),
    send: num('P95_SEND', 250),
};
const P95_DELIVERY = num('P95_DELIVERY', 250);
const ENDPOINTS = ['me', 'conversations', 'messages', 'send'];
const RS = '\u001e';

const deliveryLatency = new Trend('realtime_delivery_ms', true);
const sentForLatency = new Counter('realtime_sent');
const delivered = new Counter('realtime_delivered');
const deliveredToTarget = new Counter('realtime_delivered_to_target');
const socketErrors = new Counter('realtime_socket_errors');
const connected = new Counter('realtime_connected');

const runsReadHeavy = SCENARIO.includes('read-heavy');
const runsRealtime = SCENARIO.includes('realtime');

function readHeavyScenarios() {
    const vus = { preAllocatedVUs: Math.max(20, RATE * 2), maxVUs: Math.max(100, RATE * 10), exec: 'readHeavy', timeUnit: '1s' };
    return {
        warmup: {
            ...vus, executor: 'ramping-arrival-rate', startRate: 1,
            stages: [{ target: RATE, duration: `${RAMP_UP}s` }, { target: RATE, duration: `${WARMUP}s` }],
        },
        steady: { ...vus, executor: 'constant-arrival-rate', rate: RATE, duration: `${STEADY}s`, startTime: `${RAMP_UP + WARMUP}s` },
        rampdown: {
            ...vus, executor: 'ramping-arrival-rate', startRate: RATE, stages: [{ target: 0, duration: `${RAMP_DOWN}s` }],
            startTime: `${RAMP_UP + WARMUP + STEADY}s`,
        },
    };
}

function realtimeScenarios() {
    return {
        listeners: {
            executor: 'per-vu-iterations', exec: 'listen', vus: LISTENERS, iterations: 1,
            maxDuration: `${RT_DELAY + RT_DURATION + 60}s`,
        },
        senders: {
            executor: 'constant-arrival-rate', exec: 'sendForLatency', rate: RT_RATE, timeUnit: '1s',
            duration: `${RT_DURATION}s`, startTime: `${RT_DELAY}s`, preAllocatedVUs: Math.max(5, RT_RATE), maxVUs: RT_RATE * 10,
        },
    };
}

function thresholds() {
    const result = {};
    if (runsReadHeavy) {
        result['http_req_failed{scenario:steady}'] = ['rate<0.01'];
        result['checks{scenario:steady}'] = ['rate>0.99'];
        result['http_reqs{scenario:steady}'] = ['count>0'];
        result['http_req_duration{scenario:steady}'] = ['max>=0'];
        result['dropped_iterations{scenario:steady}'] = [`count<=${Math.ceil(RATE * STEADY * 0.01)}`];
        for (const endpoint of ENDPOINTS) {
            result[`http_req_duration{scenario:steady,endpoint:${endpoint}}`] = [`p(95)<${P95[endpoint]}`];
            result[`http_reqs{scenario:steady,endpoint:${endpoint}}`] = ['count>=0'];
        }
    }
    if (runsRealtime) {
        result.realtime_delivery_ms = [`p(95)<${P95_DELIVERY}`];
        result.realtime_socket_errors = ['count<1'];
        result['http_req_failed{scenario:senders}'] = ['rate<0.01'];
    }
    return result;
}

export const options = {
    scenarios: { ...(runsReadHeavy ? readHeavyScenarios() : {}), ...(runsRealtime ? realtimeScenarios() : {}) },
    thresholds: thresholds(),
    discardResponseBodies: true,
    summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
    setupTimeout: '5m',
};

const emailOf = (index) => `seed-${String(index).padStart(4, '0')}@chathub.test`;
const jsonHeaders = { 'Content-Type': 'application/json' };
const authorized = (token, endpoint, extra = {}) => ({
    headers: { ...jsonHeaders, Authorization: `Bearer ${token}` },
    tags: { endpoint, ...extra },
});

export function setup() {
    const indices = Array.from({ length: USERS }, (_, i) => 1 + Math.floor((i * SEED_USERS) / USERS));
    const users = [];
    for (let start = 0; start < indices.length; start += 25) {
        const logins = http.batch(indices.slice(start, start + 25).map((index) => ({
            method: 'POST',
            url: `${BASE_URL}/api/auth/login`,
            body: JSON.stringify({ identifier: emailOf(index), password: PASSWORD }),
            params: { headers: jsonHeaders, responseType: 'text', tags: { endpoint: 'login' } },
        })));
        for (const login of logins) {
            if (login.status !== 200) fail(`login failed with ${login.status}: ${login.body}`);
            const body = login.json();
            users.push({ id: body.user.id, token: body.accessToken });
        }
    }

    for (let start = 0; start < users.length; start += 25) {
        const slice = users.slice(start, start + 25);
        const pages = http.batch(slice.map((user) => ({
            method: 'GET',
            url: `${BASE_URL}/api/conversations?pageSize=50`,
            params: { ...authorized(user.token, 'setup'), responseType: 'text' },
        })));
        pages.forEach((page, i) => {
            if (page.status !== 200) fail(`conversation list failed with ${page.status}`);
            const items = page.json().items;
            slice[i].conversations = items.map((item) => item.id);
            slice[i].directs = items.filter((item) => item.type === 'Direct' && item.otherUser).map((item) => ({
                conversationId: item.id, peerId: item.otherUser.id,
            }));
            slice[i].groups = items.filter((item) => item.type === 'Group').map((item) => item.id);
        });
    }

    const active = users.filter((user) => user.conversations.length > 0);
    const pairs = runsRealtime ? pairListenersWithSenders(active) : [];
    console.log(`SETUP_DONE users=${active.length} pairs=${pairs.length}`);
    return {
        users: active.map((user) => ({ token: user.token, conversations: user.conversations })),
        pairs,
    };
}

function pairListenersWithSenders(users) {
    const tokens = new Map(users.map((user) => [user.id, user.token]));
    const pairs = [];
    for (const listener of users) {
        if (pairs.length >= LISTENERS) break;
        const direct = listener.directs.find((candidate) => tokens.has(candidate.peerId));
        if (direct) {
            pairs.push({ listenerToken: listener.token, conversationId: direct.conversationId, senderToken: tokens.get(direct.peerId) });
            continue;
        }
        for (const groupId of listener.groups.slice(0, 3)) {
            const details = http.get(`${BASE_URL}/api/conversations/${groupId}`,
                { ...authorized(listener.token, 'setup'), responseType: 'text' });
            const sender = (details.json().members || []).map((member) => member.user.id)
                .find((id) => id !== listener.id && tokens.has(id));
            if (sender) {
                pairs.push({ listenerToken: listener.token, conversationId: groupId, senderToken: tokens.get(sender) });
                break;
            }
        }
    }
    if (pairs.length === 0) fail('no listener could be paired with a logged-in sender');
    return pairs;
}

const pick = (values) => values[Math.floor(Math.random() * values.length)];

export function readHeavy(data) {
    const user = pick(data.users);
    const conversationId = pick(user.conversations);

    const me = http.get(`${BASE_URL}/api/users/me`, authorized(user.token, 'me'));
    const list = http.get(`${BASE_URL}/api/conversations`, authorized(user.token, 'conversations'));
    const messages = http.get(`${BASE_URL}/api/conversations/${conversationId}/messages`,
        authorized(user.token, 'messages', { name: 'GET /api/conversations/{id}/messages' }));
    check(me, { 'me 200': (r) => r.status === 200 });
    check(list, { 'conversations 200': (r) => r.status === 200 });
    check(messages, { 'messages 200': (r) => r.status === 200 });

    if (Math.random() < WRITE_RATIO) {
        const sent = http.post(`${BASE_URL}/api/conversations/${conversationId}/messages`,
            JSON.stringify({ type: 'Text', content: `load test ${exec.scenario.iterationInTest}` }),
            authorized(user.token, 'send', { name: 'POST /api/conversations/{id}/messages' }));
        check(sent, { 'send 201': (r) => r.status === 201 });
    }
}

export function listen(data) {
    const pairIndex = exec.scenario.iterationInTest;
    if (pairIndex >= data.pairs.length) return;
    const pair = data.pairs[pairIndex];
    const socket = new WebSocket(`${WS_URL}/hubs/chat?access_token=${encodeURIComponent(pair.listenerToken)}`);
    let handshaken = false;
    let pings;

    socket.onopen = () => {
        socket.send(JSON.stringify({ protocol: 'json', version: 1 }) + RS);
        pings = setInterval(() => socket.send(JSON.stringify({ type: 6 }) + RS), 10000);
    };
    socket.onmessage = (event) => {
        const receivedAt = Date.now();
        for (const frame of String(event.data).split(RS)) {
            if (!frame) continue;
            const message = JSON.parse(frame);
            if (!handshaken) {
                handshaken = true;
                if (message.error) socketErrors.add(1);
                else connected.add(1);
                continue;
            }
            if (message.type === 7) socketErrors.add(1);
            if (message.type !== 1 || message.target !== 'MessageReceived') continue;
            const content = message.arguments[0].content || '';
            if (!content.startsWith('lt:')) continue;
            const [, sentAt, target] = content.split(':');
            deliveryLatency.add(receivedAt - Number(sentAt));
            delivered.add(1);
            if (Number(target) === pairIndex) deliveredToTarget.add(1);
        }
    };
    socket.onerror = () => socketErrors.add(1);
    setTimeout(() => {
        clearInterval(pings);
        socket.close();
    }, (RT_DELAY + RT_DURATION + 10) * 1000);
}

export function sendForLatency(data) {
    const pairIndex = Math.floor(Math.random() * Math.min(LISTENERS, data.pairs.length));
    const pair = data.pairs[pairIndex];
    const sent = http.post(`${BASE_URL}/api/conversations/${pair.conversationId}/messages`,
        JSON.stringify({ type: 'Text', content: `lt:${Date.now()}:${pairIndex}` }),
        authorized(pair.senderToken, 'rt-send', { name: 'POST /api/conversations/{id}/messages' }));
    if (check(sent, { 'rt send 201': (r) => r.status === 201 })) sentForLatency.add(1);
}

const metric = (data, name) => (data.metrics[name] ? data.metrics[name].values : undefined);
const round = (value, digits = 1) => (value === undefined ? undefined : Number(value.toFixed(digits)));

function latencyOf(values) {
    return values && {
        p50: round(values.med), p90: round(values['p(90)']), p95: round(values['p(95)']),
        p99: round(values['p(99)']), max: round(values.max), avg: round(values.avg),
    };
}

export function handleSummary(data) {
    const failed = Object.entries(data.metrics)
        .flatMap(([name, m]) => Object.entries(m.thresholds || {}).filter(([, t]) => !t.ok).map(([expr]) => `${name} ${expr}`));
    const summary = { name: RESULT_NAME, scenario: SCENARIO, thresholdsPassed: failed.length === 0, failedThresholds: failed };

    if (runsReadHeavy) {
        const requests = metric(data, 'http_reqs{scenario:steady}');
        summary.readHeavy = {
            targetIterationsPerSecond: RATE,
            steadySeconds: STEADY,
            requests: requests ? requests.count : 0,
            requestsPerSecond: requests ? round(requests.count / STEADY) : 0,
            errorRate: round((metric(data, 'http_req_failed{scenario:steady}') || { rate: 0 }).rate, 4),
            checksRate: round((metric(data, 'checks{scenario:steady}') || { rate: 0 }).rate, 4),
            droppedIterations: (metric(data, 'dropped_iterations{scenario:steady}') || { count: 0 }).count,
            all: latencyOf(metric(data, 'http_req_duration{scenario:steady}')),
            endpoints: Object.fromEntries(ENDPOINTS.map((endpoint) => {
                const count = (metric(data, `http_reqs{scenario:steady,endpoint:${endpoint}}`) || { count: 0 }).count;
                return [endpoint, { count, rps: round(count / STEADY), ...latencyOf(metric(data, `http_req_duration{scenario:steady,endpoint:${endpoint}}`)) }];
            })),
        };
    }
    if (runsRealtime) {
        summary.realtime = {
            listeners: LISTENERS,
            sendsPerSecond: RT_RATE,
            connected: (metric(data, 'realtime_connected') || { count: 0 }).count,
            sent: (metric(data, 'realtime_sent') || { count: 0 }).count,
            delivered: (metric(data, 'realtime_delivered') || { count: 0 }).count,
            deliveredToTarget: (metric(data, 'realtime_delivered_to_target') || { count: 0 }).count,
            socketErrors: (metric(data, 'realtime_socket_errors') || { count: 0 }).count,
            sendErrorRate: round((metric(data, 'http_req_failed{scenario:senders}') || { rate: 0 }).rate, 4),
            deliveryMs: latencyOf(metric(data, 'realtime_delivery_ms')),
        };
    }

    return {
        [`results/${RESULT_NAME}.json`]: JSON.stringify(data, null, 2),
        [`results/${RESULT_NAME}.summary.json`]: JSON.stringify(summary, null, 2),
        stdout: `${formatSummary(summary)}\n`,
    };
}

function formatSummary(summary) {
    const lines = [`\n=== ${summary.name} (${summary.scenario}) ===`];
    const pad = (value, width) => String(value === undefined ? '-' : value).padStart(width);
    if (summary.readHeavy) {
        const r = summary.readHeavy;
        lines.push(`steady ${r.steadySeconds}s at ${r.targetIterationsPerSecond} it/s: ${r.requests} requests, ${r.requestsPerSecond} req/s, ` +
            `errors ${(r.errorRate * 100).toFixed(2)}%, checks ${(r.checksRate * 100).toFixed(2)}%, dropped iterations ${r.droppedIterations}`);
        lines.push(`${'endpoint'.padEnd(15)}${pad('reqs', 8)}${pad('req/s', 8)}${pad('p50', 8)}${pad('p95', 8)}${pad('p99', 8)}${pad('max', 8)}  (ms)`);
        for (const [endpoint, e] of Object.entries(r.endpoints))
            lines.push(`${endpoint.padEnd(15)}${pad(e.count, 8)}${pad(e.rps, 8)}${pad(e.p50, 8)}${pad(e.p95, 8)}${pad(e.p99, 8)}${pad(e.max, 8)}`);
        lines.push(`${'all'.padEnd(15)}${pad(r.requests, 8)}${pad(r.requestsPerSecond, 8)}${pad(r.all && r.all.p50, 8)}` +
            `${pad(r.all && r.all.p95, 8)}${pad(r.all && r.all.p99, 8)}${pad(r.all && r.all.max, 8)}`);
    }
    if (summary.realtime) {
        const r = summary.realtime;
        const d = r.deliveryMs || {};
        lines.push(`realtime: ${r.connected} sockets connected, ${r.sent} sent, ${r.deliveredToTarget} reached their target listener ` +
            `(${r.sent - r.deliveredToTarget} lost), ${r.delivered} deliveries in total, ${r.socketErrors} socket errors, ` +
            `send errors ${(r.sendErrorRate * 100).toFixed(2)}%`);
        lines.push(`delivery latency ms: p50 ${d.p50} p90 ${d.p90} p95 ${d.p95} p99 ${d.p99} max ${d.max}`);
    }
    lines.push(summary.thresholdsPassed ? 'thresholds: all passed' : `thresholds FAILED: ${summary.failedThresholds.join('; ')}`);
    return lines.join('\n');
}
