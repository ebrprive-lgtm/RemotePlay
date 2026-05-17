let currentDir=null,currentData=null,playingPath=null;
        let pollInterval=null,seekDragging=false,searchTimer=null,lastQueue=[];
        let lastVolumeBeforeMute=.7,lastBoostBeforeMute=.3;
        let seekHoldTimer=null,seekHoldInterval=null,seekHoldTriggered=false,suppressNextSeekTap=false;
        let currentPlaybackSpeed=1;
        let statusFailures=0;
        let cardHoldTimer=null,cardHoldOpened=false;
        let zoomDragging=false;
        let pendingSearchAbort=null;
        let thumbnailObserver=null;
        const queuedVideos=new Set();
        const playedVideos=new Set(loadPlayedVideos());
        const favoriteVideos=new Set();
        const installHint=document.getElementById('install-hint');
        const phoneLayoutQuery=window.matchMedia('(max-width:760px) and (pointer:coarse)');
        let isPhoneRemoteOnly=phoneLayoutQuery.matches;
        let gestureStart=null;
        applySavedTheme();
        if(!localStorage.getItem('remotePlayInstallHintDismissed')&&window.matchMedia('(display-mode: browser)').matches)installHint.style.display='flex';

        function applySavedTheme(){setTheme(localStorage.getItem('remotePlayTheme')||'default',false);}
        function setTheme(theme,save=true){
          document.body.classList.remove('theme-amoled','theme-blue','theme-sunset','theme-neon','theme-forest','theme-lavender','theme-peach','theme-mint','theme-high-contrast');
          if(theme&&theme!=='default')document.body.classList.add('theme-'+theme);
          const selector=document.getElementById('theme-select');
          if(selector)selector.value=theme||'default';
          if(save)localStorage.setItem('remotePlayTheme',theme||'default');
        }

        function applyPhonePlaybackState(isPlaying){
          if(!isPhoneRemoteOnly)return;
          document.body.classList.toggle('phone-playing',Boolean(isPlaying));
          setPlayerBarVisible(isPlaying);
        }

        function setPlayerBarVisible(visible){
          const bar=document.getElementById('now-playing-bar');
          const hdr=document.querySelector('header');
          if(bar)bar.style.display=visible?'flex':'none';
          if(hdr)hdr.classList.toggle('player-visible',Boolean(visible));
        }

        function applyDesktopDockedLayout(isPlaying){
          const useDocked=!isPhoneRemoteOnly&&Boolean(isPlaying);
          document.body.classList.toggle('desktop-player-docked',useDocked);
          document.body.classList.toggle('tablet-docked',useDocked&&window.matchMedia('(min-width:900px) and (max-width:1280px)').matches);
        }
        function applyPhoneLayout(){
          isPhoneRemoteOnly=phoneLayoutQuery.matches;
          document.body.classList.toggle('phone-remote-only',isPhoneRemoteOnly);
          if(!isPhoneRemoteOnly){
            document.body.classList.remove('phone-playing');
            document.body.classList.remove('landscape-controls');
            applyDesktopDockedLayout(Boolean(playingPath));
            if(!playingPath)setPlayerBarVisible(false);
            return;
          }

          document.body.classList.remove('desktop-player-docked');
          document.body.classList.remove('tablet-docked');
          const isLandscape=window.matchMedia('(orientation: landscape)').matches;
          document.body.classList.toggle('landscape-controls',isLandscape);
          applyPhonePlaybackState(Boolean(playingPath));
        }

        function haptic(ms){
          if(!('vibrate' in navigator))return;
          try{navigator.vibrate(ms);}catch{}
        }
        function onPlayerGestureStart(event){
          if(!isPhoneRemoteOnly||!playingPath)return;
          if(event.touches&&event.touches.length!==1)return;
          const target=event.target;
          if(target&&target.closest('button,input,select,label,a'))return;
          const p=event.changedTouches?event.changedTouches[0]:event;
          gestureStart={x:p.clientX,y:p.clientY,t:Date.now()};
        }
        function onPlayerGestureEnd(event){
          if(!gestureStart||!isPhoneRemoteOnly||!playingPath)return;
          const p=event.changedTouches?event.changedTouches[0]:event;
          const dx=p.clientX-gestureStart.x;
          const dy=p.clientY-gestureStart.y;
          const dt=Date.now()-gestureStart.t;
          gestureStart=null;
          if(dt>900)return;
          if(Math.abs(dx)<35&&Math.abs(dy)<35)return;
          if(Math.abs(dx)>Math.abs(dy)){quickSkip(dx>0?10:-10);return;}
          adjustVolumeBy(dy<0?0.05:-0.05);
        }
        function adjustVolumeBy(delta){
          const slider=document.getElementById('volume');
          if(!slider)return;
          const current=Math.max(0,Math.min(1,Number(slider.value)||0));
          const next=Math.max(0,Math.min(1,current+delta));
          slider.value=next.toFixed(2);
          setVolume(slider.value);
          haptic(8);
        }
        function startPolling(){if(pollInterval)return;pollInterval=setInterval(pollStatus,1000);}
        function stopPolling(){clearInterval(pollInterval);pollInterval=null;}

        async function pollStatus(){
          try{
            const res=await fetch('/api/status');
            if(!res.ok){setConnectionStatus('Retrying connection...',true);statusFailures++;return;}
            const s=await res.json();
            statusFailures=0;
            setConnectionStatus('Connected',false,true);
            updateDiagnosticsIndicator(s.lastError? 'error':'ok');
            const bar=document.getElementById('now-playing-bar');
            updateQueueControls(s);
            const hasQueue=Array.isArray(s.queue)&&s.queue.length>0;
            lastQueue=hasQueue?s.queue:[];
            updateStopBtn(s.isPlaying,hasQueue);
            updateAdjacentButtons(s);
            if(s.isPlaying||hasQueue){
              setPlayerBarVisible(true);
            }
            if(s.isPlaying){
              if(s.filePath&&s.filePath!==playingPath)setPlayerPoster(s.filePath);
              if(s.filePath){
                const oldPath=playingPath&&playingPath!==s.filePath?playingPath:null;
                playingPath=s.filePath;
                updatePlayingCard(s.filePath,oldPath);
              }
              const pb=document.getElementById('pause-btn');
              pb.textContent=s.isPaused?'\u25B6 Resume':'\u23F8 Pause';
              document.getElementById('player-title').textContent=(s.title||'Now playing').replace(/^\s*[▶⏸]\s*/,'');
              const optionsCard=document.getElementById('options-card');
              const volume=Math.max(0,Math.min(1,Number(s.volume)||0));
              document.getElementById('volume').value=volume;
              document.getElementById('volume-label').textContent=Math.round(volume*100)+'%';
              if(volume>0.001)lastVolumeBeforeMute=volume;
              updateVolumeIcon(volume);
              const boostAmount=Math.max(0,Math.min(1,(Number(s.audioBoost)||1)-1));
              document.getElementById('audio-boost').value=boostAmount;
              document.getElementById('audio-boost-label').textContent=Math.round(boostAmount*100)+'%';
              if(boostAmount>0.001)lastBoostBeforeMute=boostAmount;
              updateAudioBoostIcon(boostAmount);
              const brightness=Math.max(0.3,Math.min(0.7,Number(s.brightness)||0.5));
              document.getElementById('brightness').value=brightness;
              document.getElementById('brightness-label').textContent=Math.round(brightness*100)+'%';
              const rawSaturation=Number(s.saturation);
              const saturation=Math.max(0,Math.min(2,Number.isFinite(rawSaturation)?rawSaturation:1));
              document.getElementById('saturation').value=saturation;
              document.getElementById('saturation-label').textContent=Math.round(saturation*100)+'%';
              const rawZoom=Number(s.zoom);
              const zoom=Math.max(1,Math.min(2,Number.isFinite(rawZoom)?rawZoom:1));
              if(!zoomDragging){
                document.getElementById('zoom').value=zoom;
                document.getElementById('zoom-label').textContent=Math.round(zoom*100)+'%';
              }
              const err=document.getElementById('error');
              if(s.lastError){err.style.display='block';err.textContent='Playback error: '+s.lastError;}else{err.style.display='none';err.textContent='';}
              updateResumePrompt(s);
              const progress=document.getElementById('progress');
              if(s.duration>0){
                progress.max=s.duration;
                if(!seekDragging)progress.value=s.position;
              }
              document.getElementById('time-label').textContent=fmt(s.position)+' / '+fmt(s.duration);
              if(s.duration>0)updateCardProgress(s.filePath,s.position,s.duration);
              currentPlaybackSpeed=Math.max(0.5,Math.min(2,Number(s.playbackSpeed)||1));
              syncSpeedChips(currentPlaybackSpeed);
              updateTrackControls(s);
              document.getElementById('player-meta').textContent=buildPlayerMeta(s,volume,boostAmount);
              requestWakeLock();
              if(isPhoneRemoteOnly)applyPhonePlaybackState(true);
              else applyDesktopDockedLayout(true);
            }else if(isPhoneRemoteOnly){
              releaseWakeLock();
              updateResumePrompt(null);
              document.getElementById('player-title').textContent=hasQueue?'Queue ready':'Nothing playing';
              document.getElementById('player-meta').textContent=hasQueue?'Next up: '+s.queue.length+' queued video(s)':'';
              applyPhonePlaybackState(false);
            }else{
              releaseWakeLock();
              updateResumePrompt(null);
              document.getElementById('player-title').textContent=hasQueue?'Queue ready':'Nothing playing';
              document.getElementById('player-meta').textContent=hasQueue?'Next up: '+s.queue.length+' queued video(s)':'';
              if(hasQueue){
                document.body.classList.add('desktop-player-docked');
              }else{
                applyDesktopDockedLayout(false);
                setPlayerBarVisible(false);
              }
            }
          }catch(e){statusFailures++;setConnectionStatus(statusFailures>2?'Connection lost - retrying...':'Retrying connection...',true);updateDiagnosticsIndicator('error');}
        }

        function updateDiagnosticsIndicator(state){
          const dot=document.getElementById('diag-dot');
          if(!dot)return;
          dot.classList.toggle('ok',state==='ok');
          dot.classList.toggle('error',state==='error');
          dot.title=state==='ok'?'Server connected':state==='error'?'Server or playback issue':'Checking server';
        }

        async function refreshLibraryStatus(){
          try{
            const res=await fetch('/api/library-status');
            if(!res.ok)return;
            updateLibraryStatus(await res.json());
          }catch(e){}
        }

        function updateLibraryStatus(scan){
          const el=document.getElementById('scan-status');
          if(!el||!scan)return;
          const indexed=Number(scan.indexedFiles??scan.IndexedFiles)||0;
          const scanned=Number(scan.scannedFiles??scan.ScannedFiles)||0;
          const folders=Number(scan.scannedFolders??scan.ScannedFolders)||0;
          const isScanning=Boolean(scan.isScanning??scan.IsScanning);
          const error=(scan.lastError??scan.LastError??'').trim();
          el.classList.toggle('scanning',isScanning);
          el.classList.toggle('error',Boolean(error));
          el.classList.toggle('global-scan-status',isScanning);
          if(error){el.textContent='Library scan failed: '+error;return;}
          if(isScanning){el.textContent='Indexing library... '+scanned+' movie(s), '+folders+' folder(s) indexed so far. Normal playback is not interrupted.';return;}
          el.textContent=indexed>0?'Library ready: '+indexed+' indexed video(s)':'Library index not built yet';
        }

        async function refreshThumbnailStatus(){
          try{
            const res=await fetch('/api/thumbnails/status');
            if(!res.ok)return;
            updateThumbnailStatus(await res.json());
          }catch(e){}
        }
        function updateThumbnailStatus(status){
          const el=document.getElementById('thumb-status');
          if(!el||!status)return;
          const running=Boolean(status.isRunning??status.IsRunning);
          const total=Number(status.total??status.Total)||0;
          const processed=Number(status.processed??status.Processed)||0;
          const generated=Number(status.generated??status.Generated)||0;
          const cached=Number(status.cached??status.Cached)||0;
          const current=(status.currentTitle??status.CurrentTitle??'').trim();
          const error=(status.lastError??status.LastError??'').trim();
          el.classList.toggle('scanning',running);
          el.classList.toggle('error',Boolean(error&&!running));
          if(running){el.innerHTML='Generating thumbnails... '+processed+' / '+total+' ('+generated+' new, '+cached+' cached) '+esc(current)+' <button type="button" onclick="cancelThumbnailQueue()">Cancel</button>';return;}
          if(error){el.textContent=error;return;}
          el.textContent=total>0?'Thumbnails ready: '+processed+' checked, '+generated+' generated.':'';
        }
        async function startThumbnailQueue(){
          haptic(10);
          const res=await fetch('/api/thumbnails/start');
          if(res.ok)updateThumbnailStatus(await res.json());
        }
        async function cancelThumbnailQueue(){
          const res=await fetch('/api/thumbnails/cancel');
          if(res.ok)updateThumbnailStatus(await res.json());
        }

        function setConnectionStatus(message,isError,isConnected){
          const el=document.getElementById('connection-status');
          if(!el)return;
          el.textContent=message;
          el.classList.toggle('error',Boolean(isError));
          el.classList.toggle('connected',Boolean(isConnected));
        }

        function openDiagnostics(){
          document.getElementById('diagnostics-overlay').classList.add('open');
          refreshDiagnostics();
        }
        function closeDiagnostics(){document.getElementById('diagnostics-overlay').classList.remove('open');}

        function switchDiagTab(btn, tabName){
          document.querySelectorAll('.diag-tab').forEach(t=>t.classList.remove('active'));
          document.querySelectorAll('.diag-pane').forEach(p=>p.classList.remove('active'));
          btn.classList.add('active');
          const pane=document.getElementById('diag-pane-'+tabName);
          if(pane)pane.classList.add('active');
        }

        function diagRow(label,value){
          return '<dt>'+esc(label)+'</dt><dd>'+esc(String(value??'N/A'))+'</dd>';
        }
        function diagRowHtml(label,html){
          return '<dt>'+esc(label)+'</dt><dd>'+html+'</dd>';
        }

        function renderTrackCard(fields){
          return '<div class="diag-track-card">'+fields.map(([k,v])=>{
            if(v===undefined||v===null||v==='')return '';
            return '<div class="diag-track-row"><span class="diag-track-key">'+esc(k)+'</span><span class="diag-track-val">'+esc(String(v))+'</span></div>';
          }).filter(Boolean).join('')+'</div>';
        }

        async function refreshDiagnostics(){
          const content=document.getElementById('diagnostics-content');
          if(!content)return;
          content.innerHTML='<dt>Status</dt><dd>Loading...</dd>';
          ['video','audio','subtitles'].forEach(t=>{
            const el=document.getElementById('diag-'+t+'-content');
            if(el)el.innerHTML='<p class="diag-muted">Loading...</p>';
          });
          try{
            const [statusResponse,healthResponse,displayResponse,libraryResponse]=await Promise.all([
              fetch('/api/status'),fetch('/api/health'),fetch('/api/display-diagnostics'),fetch('/api/library-status')
            ]);
            const status=await statusResponse.json();
            const health=await healthResponse.json();
            const display=await displayResponse.json();
            const library=await libraryResponse.json();
            const ci=display.codecInfo;

            // ── Overview tab ────────────────────────────────────────────
            const rows=[
              ['Playback',status.isPlaying?'Playing':status.queueCount>0?'Queued':'Idle'],
              ['Title',(status.title||'').replace(/^\s*[▶⏸]\s*/,'')||'N/A'],
              ['Queue',String(status.queueCount||0)+' item(s)'],
              ['Library',library.isScanning?'Scanning '+(library.scannedFiles||0)+' video(s)':(library.indexedFiles||0)+' indexed video(s)'],
              ['Server',String(health.activeScheme||'').toUpperCase()+' port '+health.port],
              ['Display',display.targetDisplayName||'N/A'],
              ['Fullscreen repair',String(Boolean(display.needsFullscreenRepair))],
              ['Zoom',Math.round((Number(status.zoom)||1)*100)+'%'],
              ['Brightness',Math.round((Number(status.brightness)||0)*100)+'%'],
              ['Last error',status.lastError||health.startupWarning||library.lastError||'None']
            ];
            if(ci){
              rows.push(['─── Media ───','']);
              rows.push(['File',ci.fileName||'N/A']);
              rows.push(['Container',ci.containerFormat||'N/A']);
              rows.push(['Video tracks',String(ci.videoTracks?.length||0)]);
              rows.push(['Audio tracks',String(ci.audioTracks?.length||0)]);
              rows.push(['Subtitles',String(ci.subtitleTracks?.length||0)]);
              if(ci.videoTracks?.length){
                const vt=ci.videoTracks[0];
                rows.push(['Video codec',vt.codecDescription+(vt.codec&&vt.codec!==vt.codecDescription?' ('+vt.codec+')':'')]);
                rows.push(['Resolution',vt.width+'×'+vt.height]);
                rows.push(['Frame rate',vt.frameRate]);
              }
              if(ci.audioTracks?.length){
                const at=ci.audioTracks[0];
                rows.push(['Audio codec',at.codecDescription+(at.codec&&at.codec!==at.codecDescription?' ('+at.codec+')':'')]);
                rows.push(['Channels',at.channelLayout]);
                rows.push(['Sample rate',at.sampleRate+' Hz']);
              }
            }
            content.innerHTML=rows.map(row=>{
              if(row[0].startsWith('─')){
                return '<dt class="diag-section-label">'+esc(row[0])+'</dt><dd></dd>';
              }
              return diagRow(row[0],row[1]);
            }).join('');

            // ── Video tab
            const videoEl=document.getElementById('diag-video-content');
            if(videoEl){
              if(!ci||!ci.videoTracks?.length){
                videoEl.innerHTML='<p class="diag-muted">No video tracks — start a video first.</p>';
              } else {
                videoEl.innerHTML=ci.videoTracks.map((t,i)=>{
                  const title='<div class="diag-track-title">&#128250; Video track '+(i+1)+(t.description?' · '+t.description:'')+'</div>';
                  return title+renderTrackCard([
                    ['Codec',t.codecDescription+(t.codec&&t.codec!==t.codecDescription?' ('+t.codec+')':'')],
                    ['Resolution',t.width+'×'+t.height],
                    ['Frame rate',t.frameRate],
                    ['Aspect ratio (SAR)',t.aspectRatio],
                    ['Orientation',t.orientation],
                    ['Language',t.language||''],
                  ]);
                }).join('');
              }
            }

            // ── Audio tab ───────────────────────────────────────────────
            const audioEl=document.getElementById('diag-audio-content');
            if(audioEl){
              const isPlaying=Boolean(status.isPlaying);
              const swActive=Boolean(display.forceSwAudio);
              let audioFixHtml='';
              if(isPlaying){
                if(swActive){
                  audioFixHtml='<div class="diag-audio-fix diag-audio-fix--active">&#10003; Software audio decode is active for this file.</div>';
                } else {
                  audioFixHtml='<div class="diag-audio-fix"><button class="btn btn-dim" id="btn-fix-audio">&#128267; Fix silent / broken audio</button><span class="diag-muted"> &nbsp;Forces software decode &amp; restarts playback. Saved for this file.</span></div>';
                }
              }
              if(!ci||!ci.audioTracks?.length){
                audioEl.innerHTML=audioFixHtml+'<p class="diag-muted">No audio tracks — start a video first.</p>';
              } else {
                audioEl.innerHTML=audioFixHtml+ci.audioTracks.map((t,i)=>{
                  const label=t.description||t.language||('Track '+(i+1));
                  const title='<div class="diag-track-title">&#127925; Audio track '+(i+1)+' · '+esc(label)+'</div>';
                  return title+renderTrackCard([
                    ['Codec',t.codecDescription+(t.codec&&t.codec!==t.codecDescription?' ('+t.codec+')':'')],
                    ['Channels',t.channelLayout],
                    ['Sample rate',t.sampleRate?' '+t.sampleRate+' Hz':''],
                    ['Language',t.language||''],
                    ['Description',t.description||''],
                  ]);
                }).join('');
              }
              const btnFix=audioEl.querySelector('#btn-fix-audio');
              if(btnFix)btnFix.addEventListener('click',async()=>{
                btnFix.disabled=true;
                btnFix.textContent='Applying...';
                try{await api('/api/fix-audio');}catch(e){btnFix.textContent='Error — try again';}
                setTimeout(()=>refreshDiagnostics(),1500);
              });
            }

            // ── Subtitles tab
            const subsEl=document.getElementById('diag-subtitles-content');
            if(subsEl){
              if(!ci||!ci.subtitleTracks?.length){
                subsEl.innerHTML='<p class="diag-muted">No subtitle tracks in this file.</p>';
              } else {
                subsEl.innerHTML=ci.subtitleTracks.map((t,i)=>{
                  const label=t.description||t.language||('Track '+(i+1));
                  const title='<div class="diag-track-title">&#128221; Subtitle track '+(i+1)+' · '+esc(label)+'</div>';
                  return title+renderTrackCard([
                    ['Codec',t.codecDescription+(t.codec&&t.codec!==t.codecDescription?' ('+t.codec+')':'')],
                    ['Language',t.language||''],
                    ['Encoding',t.encoding||''],
                    ['Description',t.description||''],
                  ]);
                }).join('');
              }
            }

          }catch(e){
            console.error('[diag] refreshDiagnostics error:', e);
            if(content)content.innerHTML='<dt>Error</dt><dd>'+esc(String(e))+'</dd>';
          }
        }

        function fmt(sec){
          const m=Math.floor(sec/60),s=Math.floor(sec%60);
          return m+':'+(s<10?'0':'')+s;
        }

        function updateResumePrompt(status){
          const card=document.getElementById('resume-card');
          if(!card)return;
          const position=Number(status?.resumePosition)||0;
          const path=status?.filePath||'';
          const visible=Boolean(status?.isPlaying&&status?.canResume&&position>5&&path);
          card.classList.toggle('visible',visible);
          if(!visible){resumePromptPath=null;resumePromptPosition=0;return;}
          resumePromptPath=path;
          resumePromptPosition=position;
          document.getElementById('resume-detail').textContent='Continue from '+fmt(position)+' or start this video from the beginning.';
        }

        async function resumeFromSavedPosition(){
          if(!resumePromptPath||resumePromptPosition<=0)return;
          haptic(12);
          await api('/api/seek?pos='+resumePromptPosition.toFixed(1));
          updateResumePrompt(null);
          setStatus('Resumed from '+fmt(resumePromptPosition)+'.');
          await pollStatus();
        }

        async function startFromBeginning(){
          if(!resumePromptPath)return;
          const path=resumePromptPath;
          haptic(8);
          await api('/api/history/clear?path='+encodeURIComponent(path));
          await api('/api/seek?pos=0');
          unmarkPlayed(path);
          updateResumePrompt(null);
          setStatus('Starting from beginning.');
          await pollStatus();
        }

        async function onSeekDrag(){seekDragging=true;}
        async function onSeekCommit(){
          const pos=parseFloat(document.getElementById('progress').value);
          if(!Number.isNaN(pos)){
            haptic(8);
            await api('/api/seek?pos='+pos.toFixed(1));
          }
          seekDragging=false;
        }

        function updateVolumeIcon(value){
          document.getElementById('volume-icon-btn').classList.toggle('off',(Number(value)||0)<=0.001);
        }
        function updateAudioBoostIcon(value){
          document.getElementById('boost-icon-btn').classList.toggle('off',(Number(value)||0)<=0.001);
        }
        async function togglePause(){haptic(12);await api('/api/pause');}
        async function skip(seconds){await api('/api/skip?seconds='+encodeURIComponent(seconds));}
        async function quickSkip(seconds){
          if(suppressNextSeekTap){
            suppressNextSeekTap=false;
            return;
          }
          haptic(8);
          await skip(seconds);
        }
        function beginSeekHold(event,step){
          if(event.pointerType==='mouse'&&event.button!==0)return;
          seekHoldTriggered=false;
          clearTimeout(seekHoldTimer);
          clearInterval(seekHoldInterval);
          seekHoldTimer=setTimeout(()=>{
            seekHoldTriggered=true;
            suppressNextSeekTap=true;
            haptic(16);
            skip(step);
            seekHoldInterval=setInterval(()=>skip(step),220);
          },350);
        }
        function endSeekHold(){
          clearTimeout(seekHoldTimer);
          clearInterval(seekHoldInterval);
          seekHoldTimer=null;
          seekHoldInterval=null;
        }
        async function setVolume(value){
          const volume=Math.max(0,Math.min(1,parseFloat(value)||0));
          if(volume>0.001)lastVolumeBeforeMute=volume;
          document.getElementById('volume-label').textContent=Math.round(volume*100)+'%';
          updateVolumeIcon(volume);
          await api('/api/volume?value='+encodeURIComponent(volume.toFixed(2)));
        }
        async function toggleVolumeMute(){
          haptic(10);
          const slider=document.getElementById('volume');
          const current=Math.max(0,Math.min(1,parseFloat(slider.value)||0));
          const next=current>0.001?0:Math.max(0.05,lastVolumeBeforeMute||0.7);
          slider.value=next.toFixed(2);
          await setVolume(slider.value);
        }
        async function setAudioBoost(value){
          const boostAmount=Math.max(0,Math.min(1,parseFloat(value)||0));
          if(boostAmount>0.001)lastBoostBeforeMute=boostAmount;
          document.getElementById('audio-boost-label').textContent=Math.round(boostAmount*100)+'%';
          updateAudioBoostIcon(boostAmount);
          await api('/api/audio-boost?value='+encodeURIComponent((1+boostAmount).toFixed(2)));
        }
        async function toggleAudioBoostMute(){
          haptic(10);
          const slider=document.getElementById('audio-boost');
          const current=Math.max(0,Math.min(1,parseFloat(slider.value)||0));
          const next=current>0.001?0:Math.max(0.05,lastBoostBeforeMute||0.3);
          slider.value=next.toFixed(2);
          await setAudioBoost(slider.value);
        }
        async function setBrightness(value){const brightness=Math.max(0.3,Math.min(0.7,parseFloat(value)||0.5));document.getElementById('brightness').value=brightness.toFixed(2);document.getElementById('brightness-label').textContent=Math.round(brightness*100)+'%';await api('/api/brightness?value='+encodeURIComponent(brightness.toFixed(2)));}
        async function setSaturation(value){const parsed=parseFloat(value);const saturation=Math.max(0,Math.min(2,Number.isFinite(parsed)?parsed:1));document.getElementById('saturation').value=saturation.toFixed(2);document.getElementById('saturation-label').textContent=Math.round(saturation*100)+'%';await api('/api/saturation?value='+encodeURIComponent(saturation.toFixed(2)));}
        function onZoomPointerDown(){zoomDragging=true;}
        function onZoomPointerUp(){zoomDragging=false;}
        function setZoomPreview(value){const parsed=parseFloat(value);const zoom=Math.max(1,Math.min(2,Number.isFinite(parsed)?parsed:1));document.getElementById('zoom').value=zoom.toFixed(2);document.getElementById('zoom-label').textContent=Math.round(zoom*100)+'%';}
        async function commitZoom(value){setZoomPreview(value);await api('/api/zoom?value='+encodeURIComponent(parseFloat(document.getElementById('zoom').value).toFixed(2)));zoomDragging=false;}
        function resetBrightnessMid(){haptic(8);setBrightness(0.5);}
        function resetSaturationMid(){haptic(8);setSaturation(1);}
        function resetZoomDefault(){haptic(8);commitZoom(1);}
        const DEFAULT_PROFILE={volume:1,boost:0,brightness:0.5,saturation:1,zoom:1,speed:1};
        let _profileHoldTimer=null;
        let _profileHoldFired=false;
        function _getProfiles(){
          try{const s=localStorage.getItem('remotePlayProfiles');return s?JSON.parse(s):{};}catch{return {};}
        }
        function _saveProfiles(p){try{localStorage.setItem('remotePlayProfiles',JSON.stringify(p));}catch{}}
        function _getProfile(n){const p=_getProfiles();return Object.assign({},DEFAULT_PROFILE,p[n]||{});}
        function _currentSettings(){
          return{
            volume:parseFloat(document.getElementById('volume').value)||1,
            boost:parseFloat(document.getElementById('audio-boost').value)||0,
            brightness:parseFloat(document.getElementById('brightness').value)||0.5,
            saturation:parseFloat(document.getElementById('saturation').value)||1,
            zoom:parseFloat(document.getElementById('zoom').value)||1,
            speed:currentPlaybackSpeed||1
          };
        }
        function profilePointerDown(event,n){
          _profileHoldFired=false;
          clearTimeout(_profileHoldTimer);
          _profileHoldTimer=setTimeout(()=>{
            _profileHoldFired=true;
            saveProfile(n);
          },600);
        }
        function profilePointerUp(event,n){
          clearTimeout(_profileHoldTimer);
          _profileHoldTimer=null;
        }
        function profilePointerCancel(){
          clearTimeout(_profileHoldTimer);
          _profileHoldTimer=null;
        }
        function profileClick(event,n){
          if(_profileHoldFired){_profileHoldFired=false;return;}
          applyProfile(n);
        }
        function saveProfile(n){
          haptic([40,60,40]);
          const profiles=_getProfiles();
          profiles[n]=_currentSettings();
          _saveProfiles(profiles);
          const btn=document.getElementById('profile-btn-'+n);
          if(btn){
            btn.classList.remove('saving','applying');
            void btn.offsetWidth;
            btn.classList.add('saving');
            setTimeout(()=>btn.classList.remove('saving'),900);
          }
          setStatus('Profile '+n+' saved.');
        }
        async function applyProfile(n){
          const profile=_getProfile(n);
          haptic([18,30,18]);
          document.querySelectorAll('.profile-btn').forEach(b=>b.classList.toggle('active',b.dataset.profile===String(n)));
          const btn=document.getElementById('profile-btn-'+n);
          if(btn){
            btn.classList.remove('applying');
            void btn.offsetWidth;
            btn.classList.add('applying');
            setTimeout(()=>btn.classList.remove('applying'),500);
          }
          await setVolume(profile.volume);
          document.getElementById('volume').value=profile.volume.toFixed(2);
          await setAudioBoost(profile.boost);
          document.getElementById('audio-boost').value=profile.boost.toFixed(2);
          await setBrightness(profile.brightness);
          await setSaturation(profile.saturation);
          await commitZoom(profile.zoom);
          await setPlaybackSpeed(profile.speed);
          setStatus('Profile '+n+' applied.');
        }
        async function applyPlaybackProfile(name){
          // legacy no-op kept to avoid errors if called from elsewhere
        }
        function syncSpeedChips(speed){
          const chips=Array.from(document.querySelectorAll('.speed-chip'));
          let selected=null;
          for(const chip of chips){
            const value=parseFloat(chip.dataset.speed||'1');
            const isActive=Math.abs(value-speed)<0.02;
            chip.classList.toggle('active',isActive);
            if(isActive)selected=chip;
          }
          if(!selected){
            let nearest=null;
            let nearestDelta=Number.POSITIVE_INFINITY;
            for(const chip of chips){
              const value=parseFloat(chip.dataset.speed||'1');
              const delta=Math.abs(value-speed);
              if(delta<nearestDelta){nearestDelta=delta;nearest=chip;}
            }
            if(nearest)nearest.classList.add('active');
          }
        }
        async function setPlaybackSpeed(value){
          const speed=Math.max(0.5,Math.min(2,parseFloat(value)||1));
          currentPlaybackSpeed=speed;
          syncSpeedChips(speed);
          haptic(10);
          await api('/api/speed?value='+encodeURIComponent(speed.toFixed(2)));
        }
        function updateTrackControls(s){
          const audioSelect=document.getElementById('audio-track-select');
          const subtitleSelect=document.getElementById('subtitle-track-select');
          const audioGroup=document.getElementById('audio-track-group');
          const subtitleGroup=document.getElementById('subtitle-track-group');
          const trackControls=document.getElementById('track-controls');
          const audioTracks=Array.isArray(s.audioTracks)?s.audioTracks:[];
          const subtitleTracks=Array.isArray(s.subtitleTracks)?s.subtitleTracks:[];
          const showAudio=audioTracks.length>1;
          const showSubtitles=subtitleTracks.some(t=>Number(t.id)>=0);
          renderTrackSelect(audioSelect,audioTracks,s.currentAudioTrackId);
          renderTrackSelect(subtitleSelect,subtitleTracks,s.currentSubtitleTrackId);
          audioGroup.style.display=showAudio?'flex':'none';
          subtitleGroup.style.display=showSubtitles?'flex':'none';
          trackControls.style.display=(showAudio||showSubtitles)?'flex':'none';
          document.getElementById('options-card').style.display=(showAudio||showSubtitles)?'flex':'none';
        }
        function renderTrackSelect(select,tracks,currentId){
          const signature=JSON.stringify((tracks||[]).map(t=>[t.id,t.name]));
          if(select.dataset.signature!==signature){
            select.innerHTML=(tracks||[]).map(t=>'<option value="'+esc(String(t.id))+'">'+esc(t.name||('Track '+t.id))+'</option>').join('');
            select.dataset.signature=signature;
          }
          select.value=String(currentId);
        }
        function updateAdjacentButtons(s){
          const previous=document.getElementById('previous-btn');
          const next=document.getElementById('next-btn');
          const navGroup=previous.closest('.transport-nav-group');
          const queue=Array.isArray(s.queue)?s.queue:[];
          const hasQueue=queue.length>0;
          navGroup?.classList.toggle('queue-mode',hasQueue);
          previous.classList.toggle('queue-mode',hasQueue);
          next.classList.toggle('queue-mode',hasQueue);
          previous.querySelector('.nav-main').textContent='PREV';
          next.querySelector('.nav-main').textContent=hasQueue?'PLAY NEXT QUEUED':'NEXT';
          if(hasQueue){
            previous.querySelector('.nav-title').textContent='';
            next.querySelector('.nav-title').textContent=shortTitle(queue[0].title||'Queued video');
            previous.title='Previous video unavailable while queue is active';
            next.title='Play next queued: '+(queue[0].title||'Queued video');
            next.onclick=playQueueStart;
            previous.disabled=true;
            next.disabled=false;
            return;
          }
          next.onclick=()=>playAdjacent('next');
          previous.onclick=()=>playAdjacent('previous');
          const previousTitle=(s.previousTitle||'').trim();
          const nextTitle=(s.nextTitle||'').trim();
          previous.querySelector('.nav-title').textContent=previousTitle?shortTitle(previousTitle):'';
          next.querySelector('.nav-title').textContent=nextTitle?shortTitle(nextTitle):'';
          previous.title=previousTitle?('Play previous: '+previousTitle):'No previous video';
          next.title=nextTitle?('Play next: '+nextTitle):'No next video';
          previous.disabled=!previousTitle;
          next.disabled=!nextTitle;
        }
        function updateQueueControls(s){
          const card=document.getElementById('queue-card');
          const list=document.getElementById('queue-list');
          const queue=Array.isArray(s.queue)?s.queue:[];
          queuedVideos.clear();
          for(const item of queue){
            if(item.path)queuedVideos.add(item.path);
          }
          syncQueuedCards();
          if(!queue.length){list.innerHTML='<div class="queue-empty">Queue is empty</div>';return;}
          list.innerHTML=queue.map((item,index)=>{
            const path=item.path||'';
            const title=item.title||path||'Queued video';
            const thumb='/api/thumb?path='+encodeURIComponent(path);
            return '<div class="queue-item" title="'+esc(title)+'"><div class="queue-thumb" style="background-image:url('+thumb+')"></div><div class="queue-title">'+(index+1)+'. '+esc(title)+'</div><div class="queue-actions"><button onclick="moveQueueItem(\''+path+'\',\'up\')" '+(index===0?'disabled':'')+'>↑</button><button onclick="moveQueueItem(\''+path+'\',\'down\')" '+(index===queue.length-1?'disabled':'')+'>↓</button><button onclick="removeQueueItem(\''+path+'\')">✕</button></div></div>';
          }).join('');
        }
        function cardIdFor(p){return 'card-'+String(p).replace(/=/g,'_');}
        function continueCardIdFor(p){return 'continue-card-'+String(p).replace(/=/g,'_');}
        function getMovieCards(p){
          return [
            document.getElementById(cardIdFor(p)),
            document.getElementById(continueCardIdFor(p)),
            ...Array.from(document.querySelectorAll('[data-path="'+cssEscape(p)+'"]'))
          ].filter((card,index,self)=>card&&self.indexOf(card)===index);
        }
        function syncQueuedCards(){
          document.querySelectorAll('.movie-card.queued').forEach(card=>{
            const path=card.dataset.path||card.id.substring(card.id.indexOf('-')+1);
            if(!queuedVideos.has(path)){
              setQueuedCard(path,false);
            }
          });
          for(const path of queuedVideos){
            setQueuedCard(path,true);
          }
        }
        function setQueuedCard(p,isQueued){
          const cards=getMovieCards(p);
          for(const card of cards){
            card.dataset.path=p;
            card.classList.toggle('queued',isQueued);
            let badge=card.querySelector('.queue-badge');
            if(isQueued&&!badge){
              badge=document.createElement('div');
              badge.className='queue-badge queued-badge';
              badge.textContent='Queued';
              card.querySelector('.movie-title')?.insertAdjacentElement('afterend',badge);
            }else if(!isQueued&&badge){
              badge.remove();
            }

            const queueButton=findQueueButton(card);
            if(queueButton)queueButton.textContent=isQueued?'Unqueue':'Queue';
          }
        }
        function findQueueButton(card){
          return Array.from(card.querySelectorAll('.card-actions button'))
            .find(btn=>String(btn.getAttribute('onclick')||'').includes('queueCardAction'));
        }
        function shortTitle(value){return value.length>80?value.slice(0,77)+'...':value;}
        function buildPlayerMeta(s,volume,boostAmount){
          const watched=s.duration>0?Math.round((s.position/s.duration)*100)+'% watched':'';
          const bits=[watched,currentPlaybackSpeed.toFixed(2).replace(/\.00$/,'')+'x','Vol '+Math.round(volume*100)+'%','Boost '+Math.round(boostAmount*100)+'%'];
          return bits.filter(Boolean).join(' • ');
        }
        async function setAudioTrack(id){haptic(8);await api('/api/audio-track?id='+encodeURIComponent(id));}
        async function setSubtitleTrack(id){haptic(8);await api('/api/subtitle-track?id='+encodeURIComponent(id));}
        async function playAdjacent(direction){
          haptic(10);
          await api('/api/adjacent?direction='+encodeURIComponent(direction));
          // Poll until the new file is playing (up to ~2s)
          let tries=0;
          const poll=setInterval(async()=>{
            try{
              const res=await fetch('/api/status');
              if(!res.ok){if(++tries>8)clearInterval(poll);return;}
              const s=await res.json();
              if(s.isPlaying&&s.filePath&&s.filePath!==playingPath){
                clearInterval(poll);
                const oldPath=playingPath;
                playingPath=s.filePath;
                markPlayed(s.filePath);
                updatePlayingCard(s.filePath,oldPath);
              }else if(++tries>8){clearInterval(poll);}
            }catch(e){if(++tries>8)clearInterval(poll);}
          },250);
        }
        async function rescan(){
          setStatus('Refreshing search index...');
          try{
            const res=await fetch('/api/rescan');
            if(res.ok)updateLibraryStatus((await res.json()).scan);
          }catch(e){setStatus('Command failed: '+e);}
        }

        function resetCardsScrollTop(){
          const browser=document.getElementById('browser');
          if(browser)browser.scrollTop=0;
          document.documentElement.scrollTop=0;
          document.body.scrollTop=0;
          window.scrollTo(0,0);
        }
        async function loadRecent(){
          try{
            const res=await fetch('/api/recent');
            if(!res.ok)return [];
            const data=await res.json();
            return Array.isArray(data.files)?data.files:[];
          }catch(e){return [];}
        }
        async function loadFavorites(){
          try{
            const res=await fetch('/api/favorites');
            if(!res.ok)return [];
            const data=await res.json();
            const files=Array.isArray(data.files)?data.files:[];
            favoriteVideos.clear();
            files.forEach(f=>favoriteVideos.add(f.path));
            return files;
          }catch(e){return [];}
        }
        async function browse(d,offset=0,append=false){
          setStatus('Loading...');currentDir=d;document.getElementById('search').value='';
          if(!append)clearPendingThumbnails();
          try{
            const url=(d?'/api/browse?dir='+encodeURIComponent(d):'/api/browse')+(offset?'&offset='+encodeURIComponent(offset):'');
            const res=await fetch(url);
            if(!res.ok){setStatus('Server error '+res.status);return;}
            const nextData=await res.json();
            if(append&&currentData){
              currentData.files=[...(currentData.files||[]),...(nextData.files||[])];
              currentData.offset=nextData.offset;
              currentData.limit=nextData.limit;
              currentData.totalFiles=nextData.totalFiles;
              currentData.hasMoreFiles=nextData.hasMoreFiles;
            }else currentData=nextData;
            render(currentData);
            if(currentData.isRoot){
              renderFavorites(await loadFavorites());
              renderRecent(await loadRecent());
            }
            if(!append)resetCardsScrollTop();
          }catch(e){setStatus('Error: '+e);}
        }
        function setSearchBusy(on){
          const inp=document.getElementById('search');
          const sp=document.getElementById('search-spinner');
          inp.classList.toggle('searching',on);
          sp.classList.toggle('visible',on);
        }
        function onSearch(){
          const q=document.getElementById('search').value.toLowerCase().trim();
          clearTimeout(searchTimer);
          if(!q){setSearchBusy(false);if(currentData)render(currentData);return;}
          clearPendingThumbnails();
          setSearchBusy(true);
          searchTimer=setTimeout(()=>searchLibrary(q),300);
        }
        function clearPendingThumbnails(){
          if(pendingSearchAbort){
            pendingSearchAbort.abort();
            pendingSearchAbort=null;
          }
          if(thumbnailObserver){thumbnailObserver.disconnect();thumbnailObserver=null;}
          document.querySelectorAll('.movie-card').forEach(card=>{
            card.style.backgroundImage='none';
          });
        }

        function observeMovieCards(){
          if(thumbnailObserver){thumbnailObserver.disconnect();thumbnailObserver=null;}
          const cards=Array.from(document.querySelectorAll('.movie-card[data-thumb]'));
          if(!cards.length)return;
          if(!('IntersectionObserver' in window)){
            cards.forEach(card=>{card.style.backgroundImage='url('+card.dataset.thumb+')';card.removeAttribute('data-thumb');});
            return;
          }

          thumbnailObserver=new IntersectionObserver(entries=>{
            for(const entry of entries){
              if(!entry.isIntersecting)continue;
              const card=entry.target;
              const thumb=card.dataset.thumb;
              if(thumb)card.style.backgroundImage='url('+thumb+')';
              card.removeAttribute('data-thumb');
              thumbnailObserver.unobserve(card);
            }
          },{rootMargin:'420px 0px',threshold:.01});
          cards.forEach(card=>thumbnailObserver.observe(card));
        }
        async function searchLibrary(q,offset=0,append=false){
          setStatus('Searching library...');
          if(!append)clearPendingThumbnails();
          pendingSearchAbort=new AbortController();
          try{
            const res=await fetch('/api/search?q='+encodeURIComponent(q)+(offset?'&offset='+encodeURIComponent(offset):''),{signal:pendingSearchAbort.signal});
            if(!res.ok){setSearchBusy(false);setStatus('Search error '+res.status);return;}
            const data=await res.json();
            setSearchBusy(false);
            const rendered={folders:data.folders||[],files:data.files,current:'Search',currentFull:'Search',parent:null,isRoot:false,totalFiles:data.totalFiles,offset:data.offset,limit:data.limit,hasMoreFiles:data.hasMoreFiles,query:q};
            if(append&&currentData){rendered.files=[...(currentData.files||[]),...(data.files||[])];}
            currentData=rendered;
            render(rendered,true);
            updateLibraryStatus({isScanning:data.indexing,indexedFiles:data.indexedFiles,scannedFiles:data.indexedFiles,completedUtc:data.lastRefreshUtc,lastError:''});
            const note=data.indexing?' (indexing...)':'';
            setStatus((data.folders?.length||0)+' folder(s) and '+data.files.length+' result(s) from '+data.indexedFiles+' indexed video(s)'+note);
            pendingSearchAbort=null;
          }catch(e){
            setSearchBusy(false);
            if(e.name!=='AbortError')setStatus('Search failed: '+e);
            pendingSearchAbort=null;
          }
        }
        function render(data,searching){
          const bc=document.getElementById('breadcrumb');
          const countLine=document.getElementById('count-line');
          const back=document.getElementById('back-button');
          back.style.display=(!data.isRoot&&data.parent!=null&&!searching)?'block':'none';
          back.dataset.dir=(!data.isRoot&&data.parent!=null&&!searching)?data.parent:'';
          bc.innerHTML='';
          if(searching){bc.innerHTML+='<a onclick="browse(null)">&#8962; Root</a><span> &rsaquo; </span><span class="crumb-current">Search results</span>';}
          else if(data.breadcrumbs&&data.breadcrumbs.length){
            bc.innerHTML+=data.breadcrumbs.map((c,i)=>{
              const sep=i>0?'<span> &rsaquo; </span>':'';
              const label=i===0?'&#8962; Root':esc(c.name);
              const cls=i===data.breadcrumbs.length-1?' class="crumb-current"':'';
              return sep+'<a'+cls+' onclick="browse(\''+c.dir+'\')">'+label+'</a>';
            }).join('');
          }else{bc.innerHTML+='<a onclick="browse(null)">&#8962; Root</a>';}
          if(searching)countLine.textContent=(data.folders?.length||0)+' folder(s) and '+data.files.length+' result(s)';
          else countLine.textContent=data.folders.length+' folder(s), '+data.files.length+' video(s)';
          let html='';
          if(data.folders.length){
            if(searching){
              const folderLabel=data.folders.length>5?'Folders ('+data.folders.length+', scroll for more)':'Folders';
              html+='<div class="section-label">'+folderLabel+'</div>';
              html+='<div class="folder-list search-folders">';
              html+=data.folders.map(f=>'<div class="folder-row" role="button" tabindex="0" onkeydown="activateKeyboardClick(event,this)" onclick="searchLibrary(\''+esc(f.folder||f.name)+'\')">'+'<span class="folder-icon">&#128193;</span><span class="folder-name">'+esc(f.name||f.folder)+'</span></div>').join('');
              html+='</div>';
            }else{
              html+='<div class="folder-list">';
              html+=data.folders.map(f=>'<div class="folder-row" role="button" tabindex="0" onkeydown="activateKeyboardClick(event,this)" onclick="browse(\''+f.dir+'\')">'+'<span class="folder-icon">&#128193;</span><span class="folder-name">'+esc(f.name)+'</span></div>').join('');
              html+='</div>';
            }
          }
          if(data.files.length){
            html+='<div class="section-label">Videos</div><div class="movie-grid">';
            html+=data.files.map(f=>{
              const played=playedVideos.has(f.path);
              const thumbUrl='/api/thumb?path='+encodeURIComponent(f.path);
              const bg='data-thumb="'+esc(thumbUrl)+'"';
              const queued=queuedVideos.has(f.path);
              if(f.favorite)favoriteVideos.add(f.path);
              const favorite=favoriteVideos.has(f.path)||Boolean(f.favorite);
              const cardClass='movie-card '+(f.path===playingPath?'playing ':'')+(queued?'queued ':'')+(played?'played ':'')+(favorite?'favorite':'');
              const isPlaying=f.path===playingPath;
              const action=isPlaying?'':' onclick="onCardClick(event,\''+f.path+'\')"';
              const stopButton=isPlaying?'<button class="stop-btn" onclick="stopPlayingCard(event,\''+f.path+'\')">&#9632; STOP</button>':'';
              const cardActions=isPlaying?'':'<div class="card-actions"><button class="primary-action" onclick="playCardAction(event,\''+f.path+'\',\''+esc(f.name)+'\')">Play</button><button class="muted-action" onclick="queueCardAction(event,\''+f.path+'\')">'+(queued?'Unqueue':'Queue')+'</button><button class="favorite-action" onclick="toggleFavoriteCard(event,\''+f.path+'\')">'+(favorite?'Unfavorite':'Favorite')+'</button><button class="muted-action" onclick="toggleWatchedCard(event,\''+f.path+'\')">'+(played?'Unwatch':'Watched')+'</button></div>';
              const displayName=f.displayName||f.name;
              const progress=Number(f.progress)||0;
              const progressPct=Math.max(0,Math.min(99,Math.round(progress*100)));
              const progressOverlay=progressPct>0?'<div class="progress-label">'+progressPct+'%</div><div class="progress-overlay"><div class="progress-fill" style="width:'+progressPct+'%"></div></div>':'';
              const queuedBadge=queued?'<div class="queue-badge queued-badge">Queued</div>':'';
              const favoriteBadge=favorite?'<div class="favorite-badge queued-badge">Favorite</div>':'';
              return '<div class="'+cardClass+'" id="'+cardIdFor(f.path)+'" data-path="'+esc(f.path)+'" role="button" tabindex="0" aria-label="Play '+esc(displayName)+'" '+bg+action+' onkeydown="activateKeyboardClick(event,this)" onpointerdown="beginCardHold(event,\''+f.path+'\')" onpointerup="endCardHold()" onpointercancel="endCardHold()" onpointerleave="endCardHold()">'+
                '<div class="movie-card-inner">'+
                '<div class="movie-title">'+esc(displayName)+'</div>'+
                queuedBadge+
                favoriteBadge+
                stopButton+
                cardActions+
                '</div>'+
                progressOverlay+
                '</div>';
            }).join('');
            html+='</div>';}
          if(!data.folders.length&&!data.files.length)html='<div id="empty">No subfolders or video files here.</div>';
          document.getElementById('browser').innerHTML=html;
          if(data.hasMoreFiles){
            const shown=(data.files||[]).length;
            document.getElementById('browser').insertAdjacentHTML('beforeend','<button class="load-more-btn" onclick="loadMoreFiles()">Load more videos ('+shown+' of '+(data.totalFiles||shown)+')</button>');
          }
          observeMovieCards();
        }
        function loadMoreFiles(){
          if(!currentData)return;
          const nextOffset=(currentData.files||[]).length;
          if(currentData.query)searchLibrary(currentData.query,nextOffset,true);
          else browse(currentDir,nextOffset,true);
        }
        function renderRecent(files){
          if(!files.length)return;
          const browser=document.getElementById('browser');
          const continueFiles=files.filter(f=>Number(f.progress)>0&&Number(f.progress)<0.95).slice(0,6);
          if(!continueFiles.length)return;
          const html='<div class="section-label recent-toggle" onclick="toggleRecentSection(this)" style="cursor:pointer;user-select:none;">'+
            'Continue watching <span class="recent-chevron" style="font-size:0.75em;margin-left:6px;">▶</span></div>'+
            '<div class="continue-grid recent-grid" style="display:none;">'+continueFiles.map(f=>renderProgressCard(f,true)).join('')+'</div>';
          browser.innerHTML=html+browser.innerHTML;
        }
        function renderFavorites(files){
          if(!files.length)return;
          const browser=document.getElementById('browser');
          const html='<div class="section-label favorites-label">Favorites</div><div class="continue-grid recent-grid favorites-grid">'+files.slice(0,8).map(f=>renderProgressCard(f,false,true)).join('')+'</div>';
          browser.innerHTML=html+browser.innerHTML;
        }
        function renderProgressCard(f,isContinue,isFavoriteSection=false){
          const thumbUrl='/api/thumb?path='+encodeURIComponent(f.path);
          const bg='data-thumb="'+esc(thumbUrl)+'"';
          const pct=Math.max(0,Math.min(99,Math.round((Number(f.progress)||0)*100)));
          const displayName=f.displayName||f.name;
          const queued=queuedVideos.has(f.path);
          const favorite=favoriteVideos.has(f.path)||Boolean(f.favorite);
          if(favorite)favoriteVideos.add(f.path);
          return '<div class="movie-card '+(isContinue?'continue-card ':'')+(isFavoriteSection?'favorite-section-card ':'')+(queued?'queued ':'')+(favorite?'favorite ':'')+'played" id="'+continueCardIdFor(f.path)+'" data-path="'+esc(f.path)+'" role="button" tabindex="0" aria-label="Play '+esc(displayName)+'" '+bg+' onclick="onCardClick(event,\''+f.path+'\')" onkeydown="activateKeyboardClick(event,this)" onpointerdown="beginCardHold(event,\''+f.path+'\')" onpointerup="endCardHold()" onpointercancel="endCardHold()" onpointerleave="endCardHold()">'+
            '<div class="movie-card-inner"><div class="movie-title">'+esc(displayName)+'</div>'+
            (queued?'<div class="queue-badge queued-badge">Queued</div>':'')+
            (favorite?'<div class="favorite-badge queued-badge">Favorite</div>':'')+
            '<div class="movie-meta">'+pct+'% • '+esc(f.folder||'')+'</div>'+
            '<div class="card-actions"><button class="primary-action" onclick="playCardAction(event,\''+f.path+'\',\''+esc(displayName)+'\')">Play</button><button class="muted-action" onclick="queueCardAction(event,\''+f.path+'\')">'+(queued?'Unqueue':'Queue')+'</button><button class="favorite-action" onclick="toggleFavoriteCard(event,\''+f.path+'\')">'+(favorite?'Unfavorite':'Favorite')+'</button><button class="muted-action" onclick="toggleWatchedCard(event,\''+f.path+'\')">Unwatch</button></div>'+
            '</div>'+
            '<div class="progress-label">'+pct+'%</div><div class="progress-overlay"><div class="progress-fill" style="width:'+pct+'%"></div></div>'+
            '</div>';
        }
        function activateKeyboardClick(event,element){
          if(event.key!=='Enter'&&event.key!==' ')return;
          event.preventDefault();
          element.click();
        }
        function toggleRecentSection(label){
          const grid=label.nextElementSibling;
          const chevron=label.querySelector('.recent-chevron');
          const open=grid.style.display==='none';
          grid.style.display=open?'':'none';
          if(chevron)chevron.textContent=open?'▼':'▶';
        }
        function goBack(){const d=document.getElementById('back-button').dataset.dir;if(d)browse(d);}
        function onCardClick(event,p){
          if(cardHoldOpened){cardHoldOpened=false;event.preventDefault();event.stopPropagation();return;}
          play(p);
        }
        function beginCardHold(event,p){
          if(!window.matchMedia('(pointer:coarse)').matches)return;
          clearTimeout(cardHoldTimer);
          cardHoldOpened=false;
          const targetCard=event.currentTarget;
          cardHoldTimer=setTimeout(()=>{
            document.querySelectorAll('.movie-card.actions-open').forEach(card=>card.classList.remove('actions-open'));
            if(targetCard){targetCard.classList.add('actions-open');cardHoldOpened=true;haptic(18);}
          },520);
        }
        function endCardHold(){clearTimeout(cardHoldTimer);cardHoldTimer=null;}
        async function play(p,name){
          const title=name||'';
          playingPath=p;setStatus(title?'Playing: '+title:'Playing...');
          setPlayerPoster(p);
          markPlayed(p);
          updatePlayingCard(p);
          if(queuedVideos.has(p)){
            await api('/api/queue/remove?path='+encodeURIComponent(p));
            queuedVideos.delete(p);
            setQueuedCard(p,false);
          }
          await api('/api/play?path='+encodeURIComponent(p));
          startPolling();
          await pollStatus();
        }
        function setPlayerPoster(p){
          const bar=document.getElementById('now-playing-bar');
          if(!bar||!p)return;
          const thumb='/api/thumb?path='+encodeURIComponent(p);
          bar.style.background='linear-gradient(135deg,rgba(13,13,26,.92),rgba(21,21,38,.88)),url('+thumb+') center/cover';
          const poster=document.getElementById('player-poster');
          if(poster)poster.style.backgroundImage='url('+thumb+')';
        }
        function closeCardActions(event){
          const card=event?.target?.closest('.movie-card');
          if(card)card.classList.remove('actions-open');
        }
        async function playCardAction(event,p,name){
          event.stopPropagation();
          closeCardActions(event);
          await play(p,name);
        }
        async function startOverCard(event,p,name){
          event.stopPropagation();
          closeCardActions(event);
          await play(p,name);
          await api('/api/seek?pos=0');
        }
        async function queueCardAction(event,p){
          event.stopPropagation();
          closeCardActions(event);
          haptic(10);
          if(queuedVideos.has(p)){
            await removeQueueItem(p);
            return;
          }

          await api('/api/queue/add?path='+encodeURIComponent(p));
          queuedVideos.add(p);
          setQueuedCard(p,true);
          updateQueueControls({queue:Array.from(queuedVideos).map(path=>({path:path,title:path.split(/[\\/]/).pop()||path}))});
          await pollStatus();
          setStatus('Added to queue.');
        }
        async function removeQueueItem(p){
          haptic(8);
          await api('/api/queue/remove?path='+encodeURIComponent(p));
          queuedVideos.delete(p);
          setQueuedCard(p,false);
          await pollStatus();
          setStatus('Removed from queue.');
        }
        async function moveQueueItem(p,direction){
          haptic(6);
          await api('/api/queue/move?path='+encodeURIComponent(p)+'&direction='+encodeURIComponent(direction));
          await pollStatus();
        }
        async function clearQueue(){
          haptic(10);
          await api('/api/queue/clear');
          queuedVideos.clear();
          syncQueuedCards();
          updateQueueControls({queue:[]});
          setStatus('Queue cleared.');
        }
        async function toggleFavoriteCard(event,p){
          event.stopPropagation();
          closeCardActions(event);
          const favorite=!favoriteVideos.has(p);
          await api('/api/favorite?path='+encodeURIComponent(p)+'&value='+favorite);
          setFavoriteCard(p,favorite);
          setStatus(favorite?'Added to favorites.':'Removed from favorites.');
        }
        function setFavoriteCard(p,favorite){
          if(favorite)favoriteVideos.add(p);else favoriteVideos.delete(p);
          document.querySelectorAll('[data-path="'+cssEscape(p)+'"]').forEach(card=>{
            card.classList.toggle('favorite',favorite);
            const inner=card.querySelector('.movie-card-inner');
            const existingBadge=card.querySelector('.favorite-badge');
            if(favorite&&!existingBadge&&inner){
              const badge=document.createElement('div');
              badge.className='favorite-badge queued-badge';
              badge.textContent='Favorite';
              const stopButton=inner.querySelector('.stop-btn');
              inner.insertBefore(badge,stopButton||inner.querySelector('.card-actions'));
            }else if(!favorite&&existingBadge){
              existingBadge.remove();
            }
            card.querySelectorAll('.card-actions button').forEach(btn=>{
              const text=btn.textContent.trim();
              if(text==='Favorite'||text==='Unfavorite')btn.textContent=favorite?'Unfavorite':'Favorite';
            });
            if(!favorite&&card.classList.contains('favorite-section-card')){
              const grid=card.closest('.favorites-grid');
              const label=grid?.previousElementSibling;
              card.remove();
              if(grid&&!grid.querySelector('.favorite-section-card')){
                grid.remove();
                if(label?.classList.contains('favorites-label'))label.remove();
              }
            }
          });
        }
        async function toggleWatchedCard(event,p){
          event.stopPropagation();
          closeCardActions(event);
          const target=event.currentTarget;
          const shouldClear=playedVideos.has(p)||target?.textContent?.trim()==='Unwatch'||Boolean(target?.closest('.continue-card'));
          if(shouldClear){
            await api('/api/history/clear?path='+encodeURIComponent(p));
            unmarkPlayed(p);
            setStatus('Resume position cleared.');
          }else{
            markPlayed(p);
          }
        }
        async function stopPlayingCard(event,p){
          event.stopPropagation();
          if(p!==playingPath)return;
          await stop();
        }
        function updateStopBtn(isPlaying,hasQueue){
          const btn=document.getElementById('stop-btn');
          if(!btn)return;
          btn.className='btn btn-stop';
          btn.innerHTML='&#9632; STOP';
          btn.onclick=stop;
          btn.disabled=!isPlaying;
        }
        async function playQueueStart(){
          if(!lastQueue.length)return;
          const first=lastQueue[0];
          setStatus('Playing next queued: '+(first.title||'Queued video'));
          startPolling();
          await api('/api/adjacent?direction=next');
          await pollStatus();
        }
        async function stop(){
          const stoppedPath=playingPath;
          playingPath=null;
          updatePlayingCard(null,stoppedPath);
          document.getElementById('now-playing-bar').style.background='linear-gradient(135deg,#0d0d1a,#151526)';
          if(isPhoneRemoteOnly)applyPhonePlaybackState(false);
          else{
            applyDesktopDockedLayout(false);
            setPlayerBarVisible(false);
          }
          setStatus('Stopped.');
          await api('/api/stop');
        }
        async function api(url){try{await fetch(url);}catch(e){setStatus('Command failed: '+e);}}
        function loadPlayedVideos(){
          const m=document.cookie.match(/(?:^|; )playedVideos=([^;]*)/);
          if(!m)return [];
          try{return JSON.parse(decodeURIComponent(m[1]));}catch(e){return [];}
        }
        function savePlayedVideos(){
          const values=Array.from(playedVideos).slice(-1000);
          document.cookie='playedVideos='+encodeURIComponent(JSON.stringify(values))+'; max-age=31536000; path=/; SameSite=Lax';
        }
        function markPlayed(p){
          playedVideos.add(p);savePlayedVideos();
          for(const card of getMovieCards(p)){
            card.classList.add('played');
            const watchedButton=findWatchedButton(card);
            if(watchedButton)watchedButton.textContent='Unwatch';
          }
        }
        function unmarkPlayed(p){
          playedVideos.delete(p);savePlayedVideos();
          for(const card of getMovieCards(p)){
            card.classList.remove('played');
            const watchedButton=findWatchedButton(card);
            if(watchedButton)watchedButton.textContent='Watched';
            if(card.id.startsWith('continue-card-')){
              const grid=card.closest('.continue-grid');
              const label=grid?.previousElementSibling;
              card.remove();
              if(grid&&!grid.querySelector('.movie-card')){
                grid.remove();
                label?.remove();
              }
            }
          }
        }
        function findWatchedButton(card){
          return Array.from(card.querySelectorAll('.card-actions button'))
            .find(btn=>String(btn.getAttribute('onclick')||'').includes('toggleWatchedCard'));
        }
        function updateCardProgress(filePath,position,duration){
          if(!filePath||!(duration>0))return;
          const pct=Math.max(0,Math.min(99,Math.round((position/duration)*100)));
          for(const card of getMovieCards(filePath)){
            let overlay=card.querySelector('.progress-overlay');
            let label=card.querySelector('.progress-label');
            if(pct>0){
              if(!overlay){
                overlay=document.createElement('div');
                overlay.className='progress-overlay';
                const fill=document.createElement('div');
                fill.className='progress-fill';
                overlay.appendChild(fill);
                card.appendChild(overlay);
              }
              overlay.querySelector('.progress-fill').style.width=pct+'%';
              if(!label){
                label=document.createElement('div');
                label.className='progress-label';
                card.appendChild(label);
              }
              label.textContent=pct+'%';
            }else{
              overlay?.remove();
              label?.remove();
            }
          }
        }
        function updatePlayingCard(newPath,oldPath){
          const paths=[oldPath,playingPath,newPath].filter(Boolean);
          document.querySelectorAll('.movie-card.playing').forEach(c=>{if(c.dataset.path)paths.push(c.dataset.path);});
          for(const p of new Set(paths)){
            for(const card of getMovieCards(p)){
              const isActive=p===newPath;
              card.classList.toggle('playing',isActive);
              card.classList.remove('actions-open');
              card.classList.toggle('played',!isActive&&playedVideos.has(p));
              card.onclick=isActive?null:event=>onCardClick(event,p);
              const actions=card.querySelector('.card-actions');
              if(actions)actions.style.display=isActive?'none':'';
              const existing=card.querySelector('.stop-btn');
              if(isActive&&!existing){
                const btn=document.createElement('button');
                btn.className='stop-btn';
                btn.innerHTML='&#9632; STOP';
                btn.onclick=e=>stopPlayingCard(e,p);
                card.querySelector('.movie-card-inner')?.appendChild(btn);
              }else if(!isActive&&existing){
                existing.remove();
              }
            }
          }
        }
        function setStatus(m){document.getElementById('status').textContent=m;}
        function dismissInstallHint(){localStorage.setItem('remotePlayInstallHintDismissed','1');installHint.style.display='none';}
        function showUpdateAvailable(){
          let banner=document.getElementById('update-hint');
          if(!banner){
            banner=document.createElement('div');
            banner.id='update-hint';
            banner.innerHTML='<span>RemotePlay update available.</span><button type="button">Reload</button>';
            document.body.prepend(banner);
            banner.querySelector('button').onclick=()=>location.reload();
          }
          banner.style.display='flex';
        }
        function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;').replace(/'/g,'&#39;');}
        function cssEscape(s){return window.CSS&&CSS.escape?CSS.escape(String(s)):String(s).replace(/\\/g,'\\\\').replace(/"/g,'\\"');}
        let wakeLock=null;
        async function requestWakeLock(){
          if(wakeLock||!('wakeLock' in navigator))return;
          try{wakeLock=await navigator.wakeLock.request('screen');wakeLock.addEventListener('release',()=>wakeLock=null);}catch(e){}
        }
        async function releaseWakeLock(){
          if(!wakeLock)return;
          try{await wakeLock.release();}catch(e){}
          wakeLock=null;
        }
        document.addEventListener('visibilitychange',()=>{if(document.visibilityState==='visible'&&playingPath)requestWakeLock();});
        if('serviceWorker' in navigator){
          window.addEventListener('load',()=>navigator.serviceWorker.register('/service-worker.js').then(registration=>{
            registration.addEventListener('updatefound',()=>{
              const worker=registration.installing;
              if(!worker)return;
              worker.addEventListener('statechange',()=>{if(worker.state==='installed'&&navigator.serviceWorker.controller)showUpdateAvailable();});
            });
          }).catch(()=>{}));
        }
        applyPhoneLayout();
        phoneLayoutQuery.addEventListener('change',()=>{
          const wasPhone=isPhoneRemoteOnly;
          applyPhoneLayout();
          if(wasPhone&&!isPhoneRemoteOnly&&!currentData)browse(null);
        });
        window.addEventListener('orientationchange',()=>setTimeout(applyPhoneLayout,80));
        window.addEventListener('resize',()=>{if(document.body.classList.contains('desktop-player-docked'))applyDesktopDockedLayout(true);});
        document.addEventListener('keydown',(e)=>{
          if(!playingPath||e.repeat)return;
          const target=e.target;
          if(target&&(target.tagName==='INPUT'||target.tagName==='TEXTAREA'||target.contentEditable==='true'))return;
          if(e.key==='ArrowLeft'){e.preventDefault();haptic(8);skip(-10);}
          else if(e.key==='ArrowRight'){e.preventDefault();haptic(8);skip(10);}
        });
        browse(null);
        refreshLibraryStatus();
        refreshThumbnailStatus();
        setInterval(refreshLibraryStatus,2500);
        setInterval(refreshThumbnailStatus,2500);
        updateVolumeIcon(parseFloat(document.getElementById('volume').value)||0);
        updateAudioBoostIcon(parseFloat(document.getElementById('audio-boost').value)||0);
        syncSpeedChips(currentPlaybackSpeed);
        const nowPlayingBar=document.getElementById('now-playing-bar');
        if(nowPlayingBar){
          nowPlayingBar.addEventListener('touchstart',onPlayerGestureStart,{passive:true});
          nowPlayingBar.addEventListener('touchend',onPlayerGestureEnd,{passive:true});
          nowPlayingBar.addEventListener('touchcancel',()=>{gestureStart=null;},{passive:true});
        }
        startPolling();
        refreshPeers();
        setInterval(refreshPeers,8000);
        refreshVersion();
        setInterval(refreshVersion,30000);
        document.addEventListener('click',e=>{const dd=document.getElementById('peers-dropdown');if(dd.classList.contains('open')&&!e.target.closest('#peers-wrap'))closePeers();});
        document.addEventListener('pointerdown',e=>{if(!e.target.closest('.movie-card'))document.querySelectorAll('.movie-card.actions-open').forEach(c=>c.classList.remove('actions-open'));});

        // Diagnostics panel wiring
        (function wireDiagnostics(){
          const overlay=document.getElementById('diagnostics-overlay');
          const tabs=document.getElementById('diag-tabs');
          const btnRefresh=document.getElementById('btn-diag-refresh');
          const btnClose=document.getElementById('btn-diag-close');
          const btnRescan=document.getElementById('btn-rescan');
          const btnThumbs=document.getElementById('btn-thumbs');
          if(overlay)overlay.addEventListener('click',e=>{if(e.target===overlay)closeDiagnostics();});
          if(tabs)tabs.addEventListener('click',e=>{
            const btn=e.target.closest('.diag-tab');
            if(!btn)return;
            switchDiagTab(btn,btn.dataset.tab);
          });
          if(btnRefresh)btnRefresh.addEventListener('click',()=>refreshDiagnostics());
          if(btnClose)btnClose.addEventListener('click',()=>closeDiagnostics());
          if(btnRescan)btnRescan.addEventListener('click',()=>rescan());
          if(btnThumbs)btnThumbs.addEventListener('click',()=>startThumbnailQueue());
        })();

        async function refreshPeers(){
          try{
            const res=await fetch('/api/peers');
            if(!res.ok)return;
            const peers=await res.json();
            const dot=document.getElementById('peers-dot');
            const others=peers.filter(p=>!p.isSelf);
            const count=others.length;
            dot.className=count>0?'multi':'';
            const label=count>0?`Instances (${count})`:'Instances';
            document.getElementById('peers-btn').innerHTML=`<span id="peers-dot" class="${dot.className}"></span>${label}`;
            document.getElementById('peers-btn').title=count>0?`${count} other instance(s) on the network`:'No other instances found';
            renderPeersDropdown(peers);
          }catch{}
        }

        function renderPeersDropdown(peers){
          const dd=document.getElementById('peers-dropdown');
          dd.innerHTML='';
          const header=document.createElement('div');
          header.id='peers-header';
          header.textContent='RemotePlay instances';
          dd.appendChild(header);
          const self=peers.find(p=>p.isSelf);
          const others=peers.filter(p=>!p.isSelf);
          if(self){
            const el=document.createElement('div');
            el.className='peer-item peer-self';
            el.innerHTML=`<span class="peer-dot"></span><span class="peer-info"><div class="peer-name">${esc(self.name)}</div><div class="peer-addr">${esc(self.host)}:${self.port}</div></span><span class="peer-badge">this</span>`;
            dd.appendChild(el);
          }
          if(others.length===0){
            const el=document.createElement('div');
            el.className='peer-no-others';
            el.textContent='No other instances discovered yet.';
            dd.appendChild(el);
          }else{
            others.forEach(p=>{
              const a=document.createElement('a');
              a.className='peer-item';
              a.href=p.url;
              a.title=`Switch to ${esc(p.name)} — ${p.url}`;
              a.innerHTML=`<span class="peer-dot"></span><span class="peer-info"><div class="peer-name">${esc(p.name)}</div><div class="peer-addr">${esc(p.host)}:${p.port}</div></span>`;
              dd.appendChild(a);
            });
          }
        }

        function togglePeers(){
          const dd=document.getElementById('peers-dropdown');
          const btn=document.getElementById('peers-btn');
          const isOpen=dd.classList.toggle('open');
          btn.setAttribute('aria-expanded',String(isOpen));
          if(isOpen)refreshPeers();
        }

        function closePeers(){
          document.getElementById('peers-dropdown').classList.remove('open');
          document.getElementById('peers-btn').setAttribute('aria-expanded','false');
        }

        let _loadedVersion=null;
        async function refreshVersion(){
          try{
            const res=await fetch('/api/version');
            if(!res.ok)return;
            const v=await res.json();
            const chip=document.getElementById('version-chip');
            if(chip&&v.version)chip.textContent='v'+v.version;
            const banner=document.getElementById('update-banner');
            if(banner)banner.style.display=v.isUpdating?'block':'none';
            if(v.version){
              if(_loadedVersion===null){
                _loadedVersion=v.version;
              }else if(_loadedVersion!==v.version){
                // Server was updated while this page was open — force a full reload to pick up new assets
                if('serviceWorker' in navigator){
                  const reg=await navigator.serviceWorker.getRegistration();
                  if(reg)await reg.update();
                }
                location.reload(true);
                return;
              }
            }
          }catch{}
        }
