// Run manually against an isolated, pre-populated environment; not part of CI.
import http from 'k6/http';
import { check, sleep } from 'k6';

const sizes = { 30: 2, 100: 5, 300: 10, 500: 15 };
const size = Number(__ENV.VIGILOOM_DEVICES);
const base = (__ENV.VIGILOOM_URL || '').replace(/\/$/, '');
const token = __ENV.VIGILOOM_API_TOKEN;
if (!sizes[size] || !base || !token) {
  throw new Error('Set VIGILOOM_DEVICES (30, 100, 300 or 500), VIGILOOM_URL and VIGILOOM_API_TOKEN.');
}
if (!/^https:\/\//i.test(base) && !/^http:\/\/(localhost|127\.0\.0\.1|\[::1\])(:\d+)?$/i.test(base)) {
  throw new Error('Use HTTPS, or HTTP only for a loopback test host.');
}
const params = { headers: { Authorization: `Bearer ${token}` }, redirects: 0 };
const url = `${base}/api/v1/workspace/overview`;

export const options = {
  vus: sizes[size],
  duration: __ENV.VIGILOOM_DURATION || '5m',
  summaryTrendStats: ['count', 'avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'],
  thresholds: { checks: ['rate==1'] },
};

export function setup() {
  const response = http.get(url, params);
  if (response.status !== 200) throw new Error(`Overview preflight failed: HTTP ${response.status}`);
  const data = response.json();
  if (data.ping.unavailable || data.agents.unavailable || data.services.unavailable || data.groupsUnavailable || data.ping.total !== size) {
    throw new Error('Overview is unavailable or does not contain the requested number of devices.');
  }
}

export default function () {
  const response = http.get(url, params);
  check(response, {
    'overview available': (r) => {
      if (r.status !== 200) return false;
      const data = r.json();
      return !data.ping.unavailable && !data.agents.unavailable && !data.services.unavailable && !data.groupsUnavailable && data.ping.total === size;
    },
  });
  sleep(5);
}
