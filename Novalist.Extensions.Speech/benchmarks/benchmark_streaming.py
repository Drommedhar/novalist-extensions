import contextlib, json, os, shutil, sys, time, wave
from pathlib import Path
root=Path.home()/'Library/Application Support/Novalist/extensions/com.novalist.speech'
os.environ.update(HF_HOME=str(root/'models'),HF_HUB_OFFLINE='1',TRANSFORMERS_OFFLINE='1')
sys.path.insert(0,str(Path('Novalist.Extensions.Speech/python').resolve()))
import sidecar
import mlx.core as mx
work=Path('artifacts/speech-stream/work');work.mkdir(exist_ok=True)
shutil.copyfile('artifacts/speech-m5/design-fp32/design-0.wav',work/'voice.wav')
results=[]
with contextlib.redirect_stdout(sys.stderr):
 engine=sidecar.new_engine(); sidecar.ensure_clone(engine)
short='Am nächsten Morgen öffnete sie das Fenster. Die Straße war still, und aus dem Garten kam der Duft von frischem Regen.'
long=Path('artifacts/speech-mlx/long.txt').read_text()
for kind,text,rate in [('short',short,1),('short',short,1),('short',short,1),('long',long,1),('long',long,1),('speed',short,0.9)]:
 sidecar.seed_engine(engine,42);mx.reset_peak_memory();began=time.perf_counter();events=[]
 def emit(**kw):
  if kw['type'] in ('chunk','clip','error'):
   events.append(dict(kw,seconds=time.perf_counter()-began))
 sidecar.emit=emit
 sidecar.CURRENT_ID=str(len(results))
 request=dict(language='de',stream=True,rate=rate,voices={'voice':'voice.wav'},voiceTexts={'voice':sidecar.design_text('de')},segments=[dict(key=kind,text=text,voiceId='voice')])
 sidecar.do_render(engine,str(work),request)
 assert not any(e['type']=='error' for e in events), events
 clip=next(e for e in events if e['type']=='clip');chunks=[e for e in events if e['type']=='chunk']
 def pcm(name):
  with wave.open(str(work/name),'rb') as f:return f.readframes(f.getnframes())
 if chunks:assert b''.join(pcm(c['file']) for c in chunks)==pcm(clip['file'])
 else:assert rate!=1
 row=dict(kind=kind,rate=rate,seconds=clip['seconds'],audio_seconds=clip['durationMs']/1000,first_chunk_seconds=chunks[0]['seconds'] if chunks else None,chunks=len(chunks),active_memory=mx.get_active_memory(),peak_memory=mx.get_peak_memory())
 print(json.dumps(row),flush=True);results.append(row)
 Path('artifacts/speech-stream/results.json').write_text(json.dumps(results,indent=2)+'\n')
print('PASS: emitted chunk PCM exactly matches final cached waveform; speed override stays buffered.')
