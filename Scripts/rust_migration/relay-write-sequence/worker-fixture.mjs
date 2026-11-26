import { RelayObject } from './ENTRY';
export {default} from './ENTRY';
import { digest } from './src/crypto.mjs';
const now = 1790000000;
export class Fixture extends RelayObject {
  constructor(ctx,env) {
    super(ctx,env);
    this.relay.now=()=>now;this.relay.budget.now=()=>now;this.relay.schedule=()=>{};
    this.rereads=0;this.projected=0;
    const store=this.relay.store,all=store.sql.all;
    store.sql.all=(q,...params)=>{if(q==='SELECT data FROM devices WHERE owner=?')this.rereads++;return all(q,...params);};
    const project=this.relay.snapshotRead;
    if(project)this.relay.snapshotRead=(...args)=>{const p=project(...args);if(p)this.projected++;return p;};
  }
  async fetch(request) {
    const url=new URL(request.url),store=this.relay.store;
    if(url.pathname==='/seed') {
      const shape=await request.json();
      for(const owner of ['example','other']) {
        store.saveUser(owner,{preferences:{providerIDs:[]},dataFloor:owner==='example'?40:4000});
        for(let i=0;i<(owner==='example'?shape.devices:1);i++) {
          const id=owner+'-'+i,session=digest('fixture-'+id);
          store.run('INSERT INTO sessions VALUES (?,?,?,?)',session,owner,'desktop',now+(i===2?-10:366*86400));
          store.saveDevice(owner,{id,session,name:'initial-'+i,platform:'macOS',seq:i===2?84:i,onlineWindow:660,
            receivedAt:i===0?0:now,snapshot:shape.snapshot});
        }
      }
      store.run('INSERT INTO pairs VALUES (?,?,?,?,?)','fixture-pair','fixture-hash','example',JSON.stringify({session:digest('fixture-example-0')}),now+1000);
      return new Response('ok');
    }
    if(url.pathname==='/inspect') {
      await this.relay.settle();
      return Response.json({rows:['example','other'].map(owner=>({ owner, user:store.user(owner),
        devices:store.devices(owner,now).map(d=>[d.id,d.seq,d.name,d.receivedAt,d.snapshot.providers[0]?.todayTokens])})),
        pairs:store.sql.all('SELECT id FROM pairs ORDER BY id'),rereads:this.rereads,projected:this.projected});
    }
    return super.fetch(request);
  }
}
