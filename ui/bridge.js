const host = window.chrome?.webview;
const requests = new Map();
let nextId = 1;

host?.addEventListener('message', ({data}) => {
  if (data.event === 'inactive') {
    window.dispatchEvent(new Event('vault-inactive'));
    return;
  }
  const request = requests.get(data.id);
  if (!request) return;
  requests.delete(data.id);
  if (data.ok) request.resolve(data.value);
  else request.reject(new Error(data.error || '操作失败，请重试。'));
});

export function native(method, payload = null) {
  if (!host) return Promise.reject(new Error(`请双击「${document.title}.exe」打开桌面应用。`));
  const id = nextId++;
  return new Promise((resolve, reject) => {
    requests.set(id, {resolve, reject});
    try {
      host.postMessage({id, method, payload});
    } catch (error) {
      requests.delete(id);
      reject(error);
    }
  });
}
