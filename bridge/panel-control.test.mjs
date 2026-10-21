import test from 'node:test';
import assert from 'node:assert/strict';
import { startBridge } from './bridge.mjs';
import sample from './config.example.json' with { type: 'json' };

for (const provider of ['OpenShock', 'PiShock']) {
  test(`${provider}: panel commands expire, cap absolute values, preserve pause/cooldown and never exceed 15 seconds`, async () => {
    const config=structuredClone(sample); config.provider=provider; config.dryRun=true; config.piShockUsername='fixture-user';
    const target={...config.shockers[0],key:'fixture',uuid:provider==='PiShock' ? '42/7' : '',cooldownMs:0,
      manualMinIntensity:0,manualMaxIntensity:40,manualMinDurationMs:300,manualMaxDurationMs:2000,
      minIntensity:0,maxIntensity:12,maxDurationMs:800};
    config.shockers=[target]; config.piShockShockers=[target];
    const logs=[]; const bridge=await startBridge(config,'',{externalOsc:true,log:line=>logs.push(line)});
    const count=()=>logs.filter(l=>l.includes('TEST ONLY:')).length;
    const send=()=>bridge.controlPanel('fixture','Shock',80,15000,Date.now()+200);
    try {
      assert.equal(await bridge.controlPanel('fixture','Shock',80,1000,Date.now()-1),false);
      assert.equal(await bridge.controlPanel('fixture','Shock',80,1000,Date.now()+1000),false);
      assert.equal(await bridge.controlPanel('fixture','Shock',80,16000,Date.now()+200),false);
      assert.equal(await bridge.controlPanel('fixture','Shock',0,1000,Date.now()+200),false);
      assert.equal(count(),0);
      assert.equal(await send(),true); assert.equal(count(),1);
      assert.match(logs.find(l=>l.includes('TEST ONLY:')),/Shock 40% for 2000 ms/);
      target.paused=true; bridge.configure(config); assert.equal(await send(),false); assert.equal(count(),1);
      target.paused=false; target.cooldownMs=60000; bridge.configure(config); assert.equal(await send(),false); assert.equal(count(),1);
      target.cooldownMs=0; target.manualMinDurationMs=16000; target.manualMaxDurationMs=20000;
      bridge.configure(config); assert.equal(await send(),false); assert.equal(count(),1);
    } finally { await bridge(); }
  });
}
