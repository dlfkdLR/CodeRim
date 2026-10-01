// Fronts the relay on a *.pages.dev address. *.workers.dev is blocked by some Korean
// networks, while pages.dev is not. The relay itself lives in the Durable Object.
export async function onRequest({ request, env }) {
  if (new URL(request.url).protocol !== 'https:') return new Response('{"error":"https_required"}', { status: 400 });
  return env.RELAY.get(env.RELAY.idFromName('relay')).fetch(request);
}
