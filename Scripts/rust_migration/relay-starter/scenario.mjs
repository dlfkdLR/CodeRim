// Deterministic product-side inputs shared by Node SQLite and the local Durable Object.
export const epoch = 1790000000;
const phone = i => 'fixture-phone-' + i;
const remote = i => (16 + i).toString(16).repeat(32);
function state(now, phase, count) {
  return { schemaVersion: 1, generatedAt: now,
    providers: Array.from({length: count}, (_, i) => ({ id: 'p-' + i, name: 'Service ' + i,
      state: 'ready', localState: 'ready', windows: [{name:'Weekly', remainingPercent:72, resetsAt:now+3600}],
      todayTokens:i+4, updatedAt:now })),
    sessions: phase ? [{providerID:'p-0', phase, title:'Fixture', since:now-10}] : [] };
}
export function prepare({store, digest}, phones, count, mixed = true) {
  store.saveUser('example', {preferences:{providerIDs:[]}});
  const desktop = digest('fixture-desktop');
  store.run('INSERT INTO sessions VALUES (?,?,?,?)', desktop, 'example', 'desktop', epoch + 86400);
  store.saveDevice('example', {id:'mac',session:desktop,name:'Fixture',platform:'macOS',seq:2,receivedAt:epoch,
    snapshot:state(epoch,'working',count)});
  for(let i=0;i<phones;i++) {
    const session=digest(phone(i));
    store.run('INSERT INTO sessions VALUES (?,?,?,?)',session,'example','mobile',epoch+86400);
    store.saveStarter({owner:'example',session,data:{token:remote(i), startedAt:epoch-400,
      ...(mixed && i % 6 === 2 ? {suppressed:true} : {})}});
    if (!mixed || i % 6 === 0) store.saveActivity({id:'a'+i,owner:'example',session,
      data:{token:'update-'+i,createdAt:epoch-5,sentAt:0,ending:false}});
  }
}
export function observe(store) {
  return Object.fromEntries(['activities','starters','devices','users','sessions','views'].map(table => [
    table,store.sql.all('SELECT * FROM '+table+' ORDER BY rowid').map(row => row.data ? {...row,data:JSON.parse(row.data)} : row)
  ]));
}
export function instrument(relay, clock, sent, alarms) {
  relay.now=()=>clock.now;
  relay.budget.now=()=>clock.now;
  relay.schedule=at=>{alarms.push(at);};
  relay.pushConfigured=true;
  relay.push=async (token,body,options)=>{
    sent.push([body,options]);
    if (body.aps.event === 'start' && clock.now===epoch) {
      if(token===remote(1))return {status:503};
      if(token===remote(3))return {status:410};
      if(token===remote(4))throw Error('synthetic unavailable transport');
    }
    return {status:200};
  };
}
export function fixtureRequest(method,path,body,token) {
  return new Request('https://relay.invalid'+path,{method,headers:{'content-type':'application/json',
    ...(token ? {authorization:'Bearer '+token} : {})},body:body===undefined ? undefined : JSON.stringify(body)});
}
export async function replay(host) {
  const {clock,digest,send,sent,alarms}=host;
  prepare(host,7,5);
  // This expired row must be discarded even though it owns a live Activity.
  const expired=digest(phone(6));
  host.store.run('UPDATE sessions SET expires=? WHERE hash=?',epoch,expired);
  // Missing registrations have historically been valid stops, without any APNs token.
  host.store.saveStarter({owner:'example',session:digest(phone(5)),data:{suppressed:true}});
  const history=[];
  async function step(label, request) {
    sent.length=0;alarms.length=0;
    const response=request ? await send(request) : undefined;
    await host.relay.settle();await host.relay.alarm();
    history.push({label,http:response && [response.status,await response.json()],sent:[...sent],
      alarms:[...alarms],rows:observe(host.store)});
  }
  await step('first pass',fixtureRequest('GET','/health'));
  clock.now+=60;await step('transient retries');
  clock.now=epoch+300;await step('cooldown exact');
  clock.now=epoch+661;await step('desktop becomes offline');
  clock.now++;await step('idle connection releases Stop',
    fixtureRequest('POST','/v1/snapshot',state(clock.now,null,5),'fixture-desktop'));
  clock.now+=20;await step('waiting session',
    fixtureRequest('POST','/v1/snapshot',state(clock.now,'waiting',5),'fixture-desktop'));
  clock.now++;await step('phone registers new starter',
    fixtureRequest('POST','/v1/push-to-start',{pushToken:'ab'.repeat(32)},phone(5)));
  return history;
}
