let currentDir=null,currentData=null,playingPath=null;
        let browseHistory=[];
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
              if(playingPath){updatePlayingCard(null,playingPath);playingPath=null;}
              document.getElementById('player-title').textContent=hasQueue?'Queue ready':'Nothing playing';
              document.getElementById('player-meta').textContent=hasQueue?'Next up: '+s.queue.length+' queued video(s)':'';
              applyPhonePlaybackState(false);
            }else{
              releaseWakeLock();
              updateResumePrompt(null);
              if(playingPath){updatePlayingCard(null,playingPath);playingPath=null;}
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
          if(currentMode!=='video')return;
          try{
            const res=await fetch('/api/library-status');
            if(!res.ok)return;
            updateLibraryStatus(await res.json());
          }catch(e){}
        }

        function updateLibraryStatus(scan){
          if(currentMode!=='video')return;
          const el=document.getElementById('scan-status');
          if(!el||!scan)return;
          const indexed=Number(scan.indexedFiles??scan.IndexedFiles)||0;
          const movies=Number(scan.indexedMovies??scan.IndexedMovies??indexed)||0;
          const links=Number(scan.indexedLinks??scan.IndexedLinks)||0;
          const scanned=Number(scan.scannedFiles??scan.ScannedFiles)||0;
          const folders=Number(scan.scannedFolders??scan.ScannedFolders)||0;
          const isScanning=Boolean(scan.isScanning??scan.IsScanning);
          const error=(scan.lastError??scan.LastError??'').trim();
          el.classList.toggle('scanning',isScanning);
          el.classList.toggle('error',Boolean(error));
          el.classList.toggle('global-scan-status',isScanning);
          if(error){el.textContent='Library scan failed: '+error;return;}
          if(isScanning){el.textContent='Indexing library... '+scanned+' movie(s), '+folders+' folder(s) indexed so far. Normal playback is not interrupted.';return;}
          if(indexed>0){
            const linkPart=links>0?' — '+links+' link(s)':'';
            el.textContent='Library ready: '+movies+' movie(s)'+linkPart;
          }else{
            el.textContent='Library index not built yet';
          }
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
              ['Library',library.isScanning?'Scanning '+(library.scannedFiles||0)+' video(s)':((library.indexedMovies||library.indexedFiles||0))+' movie(s)'+(library.indexedLinks>0?' \u2014 '+(library.indexedLinks)+' link(s)':'')],
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
        let currentIsLinkedDir=false;
        async function browse(d,offset=0,append=false,pushHistory=true,isLinked=false){
          // Push the current location onto the back-stack before navigating (not for pagination)
          if(!append&&pushHistory&&currentDir!==d&&currentDir!==null)
            browseHistory.push(currentDir);
          if(!append&&d===null)
            browseHistory=[];
          if(!append)currentIsLinkedDir=isLinked;
          setStatus('Loading...');currentDir=d;document.getElementById('search').value='';
          if(!append){clearPendingThumbnails();setBrowseLoading(true);}
          try{
            const url=(d?'/api/browse?dir='+encodeURIComponent(d):'/api/browse')+(offset?'&offset='+encodeURIComponent(offset):'');
            const res=await fetch(url);
            if(!res.ok){setBrowseLoading(false);setStatus('Server error '+res.status);return;}
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
          finally{setBrowseLoading(false);}
        }
        function setBrowseLoading(on){
          document.getElementById('browse-loading').classList.toggle('visible',on);
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
          if(currentMode==='music'){
            if(!q){if(currentMusicData)renderMusic(currentMusicData);else browseMusic(null);return;}
            searchTimer=setTimeout(()=>searchMusicLibrary(q),300);
            return;
          }
          if(currentMode==='radio')return;
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
        let currentMode='video';
                let musicBrowseHistory=[];
                let currentMusicFolder=null;
                let currentMusicData=null;
                let musicStatusPollTimer=null;
                let musicPlaybackPollTimer=null;
                let musicCurrentPath=null;
                let musicIsPlaying=false;
                let musicCurrentVolume=0.8;
                // Track list for prev/next
                let musicTrackList=[];
                let musicTrackIndex=-1;
                // Playback options
                let _musicAutoPlay=true;
                let _musicShuffle=false;
                let _musicShuffleOrder=[];  // shuffled indices into musicTrackList
                let _musicShufflePos=-1;

                // ── Music playback ─────────────────────────────────────────────────────
                let _musicTrackListFolder=null;  // folder the current musicTrackList was built from
                async function playMusic(path,name){
                  musicCurrentPath=path;
                  // Update the .playing highlight immediately on click
                  document.querySelectorAll('.music-track-card').forEach(card=>{
                    card.classList.toggle('playing',card.dataset.path===path);
                  });
                  // Build/update track list from current browse data for prev/next.
                  // Only rebuild shuffle order when the folder (track list) changes.
                  if(currentMusicData&&currentMusicData.files&&currentMusicData.files.length){
                    const newFolder=currentMusicData.folder||currentMusicFolder||null;
                    const folderChanged=newFolder!==_musicTrackListFolder;
                    _musicTrackListFolder=newFolder;
                    musicTrackList=currentMusicData.files.map(f=>({path:f.path,name:f.name||pathToName(f.path)}));
                    musicTrackIndex=musicTrackList.findIndex(t=>t.path===path);
                    if(_musicShuffle&&folderChanged)_buildShuffleOrder();
                    else if(_musicShuffle&&_musicShuffleOrder.length){
                      // update shuffle position to match the new track
                      const pos=_musicShuffleOrder.indexOf(musicTrackIndex);
                      if(pos>=0)_musicShufflePos=pos;
                    }
                  }
                  await fetch('/api/music/play?path='+encodeURIComponent(path));
                  musicIsPlaying=true;
                  const displayName=name||(musicTrackIndex>=0?musicTrackList[musicTrackIndex].name:null)||pathToName(path);
                  updateMusicBar({isPlaying:true,isPaused:false,title:displayName,position:0,duration:0});
                  startMusicPlaybackPoll();
                }

                async function musicToggle(){
                  await fetch('/api/music/pause');
                  // state updated by next poll tick
                }

                async function musicStop(){
                  await fetch('/api/music/stop');
                  musicIsPlaying=false;
                  musicCurrentPath=null;
                  const bar=document.getElementById('music-player-bar');
                  if(bar)bar.style.display='none';
                  document.body.classList.remove('music-player-docked');
                  stopMusicPlaybackPoll();
                }

                async function musicPrev(){
                  if(musicTrackList.length===0)return;
                  let t;
                  if(_musicShuffle&&_musicShuffleOrder.length){
                    _musicShufflePos=Math.max(0,_musicShufflePos-1);
                    t=musicTrackList[_musicShuffleOrder[_musicShufflePos]];
                  }else{
                    const idx=Math.max(0,musicTrackIndex-1);
                    t=musicTrackList[idx];
                  }
                  if(t)await playMusic(t.path,t.name);
                }

                async function musicNext(){
                  if(musicTrackList.length===0)return;
                  const t=_musicNextTrack();
                  if(t)await playMusic(t.path,t.name);
                }

                async function musicVolume(v){
                  musicCurrentVolume=Number(v);
                  const lbl=document.getElementById('music-volume-label');
                  if(lbl)lbl.textContent=Math.round(Number(v)*100)+'%';
                  await fetch('/api/music/volume?v='+encodeURIComponent(v));
                }

                function pathToName(p){
                  if(!p)return '';
                  const parts=p.replace(/\\/g,'/').split('/');
                  const file=parts[parts.length-1]||'';
                  return file.replace(/\.[^.]+$/,'');
                }

                function updateMusicBar(s){
                  const bar=document.getElementById('music-player-bar');
                  if(!bar)return;
                  const active=s.isPlaying||s.isPaused;
                  bar.style.display=active?'flex':'none';
                  // Dock/undock alongside browser (mirrors desktop-player-docked for video)
                  const isDocked=active&&window.matchMedia('(min-width:900px)').matches;
                  document.body.classList.toggle('music-player-docked',isDocked);

                  // Title & meta
                  const title=document.getElementById('music-bar-title');
                  if(title)title.textContent=s.title||'—';
                  const meta=document.getElementById('music-bar-meta');
                  if(meta)meta.textContent=s.artist||(s.albumArtist?s.albumArtist:'')||'';

                  // Prev / Next nav titles (shuffle-aware)
                  _refreshMusicNavLabels();

                  // Seek bar
                  const seek=document.getElementById('music-seek');
                  if(seek&&!_musicSeekDragging){
                    seek.max=s.duration>0?s.duration:0;
                    seek.value=s.position||0;
                  }

                  // Time label
                  const timeEl=document.getElementById('music-time-label');
                  if(timeEl)timeEl.textContent=s.duration>0?fmtSec(s.position)+' / '+fmtSec(s.duration):'0:00 / 0:00';

                  // Play button label
                  const btn=document.getElementById('music-btn-play');
                  if(btn)btn.innerHTML=s.isPaused?'&#9654; Play':'&#9646;&#9646; Pause';

                  if(s.lastError&&!active)bar.style.display='none';
                }

                let _musicSeekDragging=false;
                function onMusicSeekDrag(){_musicSeekDragging=true;}
                async function onMusicSeekCommit(){
                  if(!_musicSeekDragging)return;  // ignore programmatic value changes
                  _musicSeekDragging=false;
                  const seek=document.getElementById('music-seek');
                  if(!seek)return;
                  const pos=parseFloat(seek.value);
                  if(Number.isNaN(pos))return;
                  try{await fetch('/api/music/seek?pos='+pos.toFixed(2),{method:'POST'});}catch(e){}
                }

                function toggleMusicAutoPlay(){
                  _musicAutoPlay=!_musicAutoPlay;
                  const btn=document.getElementById('music-btn-autoplay');
                  if(btn)btn.classList.toggle('active',_musicAutoPlay);
                }
                function _buildShuffleOrder(){
                  const n=musicTrackList.length;
                  const arr=Array.from({length:n},(_,i)=>i);
                  for(let i=n-1;i>0;i--){const j=Math.floor(Math.random()*(i+1));[arr[i],arr[j]]=[arr[j],arr[i]];}
                  // Put current track first so shuffle starts from the current song
                  const cur=arr.indexOf(musicTrackIndex);
                  if(cur>0){const tmp=arr[0];arr[0]=arr[cur];arr[cur]=tmp;}
                  _musicShuffleOrder=arr; _musicShufflePos=0;
                }
                function toggleMusicShuffle(){
                  _musicShuffle=!_musicShuffle;
                  const btn=document.getElementById('music-btn-shuffle');
                  if(btn)btn.classList.toggle('active',_musicShuffle);
                  if(_musicShuffle){
                    // Load ALL tracks in the current folder before building shuffle order so
                    // the shuffle pool is the full folder, not just the first page.
                    _loadAllMusicTracksForShuffle().then(()=>{
                      _buildShuffleOrder();
                      // Refresh nav-label display without touching server state
                      _refreshMusicNavLabels();
                    });
                  }else{
                    _refreshMusicNavLabels();
                  }
                }
                // Silently fetches remaining pages until hasMore is false; merges into musicTrackList.
                async function _loadAllMusicTracksForShuffle(){
                  if(!currentMusicData)return;
                  // If we already have all tracks (or it's a search result) nothing to do
                  while(currentMusicData.hasMore){
                    const offset=currentMusicData.files?currentMusicData.files.length:0;
                    try{
                      const folder=currentMusicData.folder||currentMusicFolder||null;
                      const url='/api/music/browse'+(folder?'?folder='+encodeURIComponent(folder):'?')+'&offset='+encodeURIComponent(offset);
                      const res=await fetch(url);
                      if(!res.ok)break;
                      const page=await res.json();
                      currentMusicData.files=[...(currentMusicData.files||[]),...(page.files||[])];
                      currentMusicData.offset=page.offset;
                      currentMusicData.hasMore=page.hasMore;
                    }catch(e){break;}
                  }
                  // Rebuild the in-memory track list from the now-complete file list
                  musicTrackList=currentMusicData.files.map(f=>({path:f.path,name:f.name||pathToName(f.path)}));
                  musicTrackIndex=musicTrackList.findIndex(t=>t.path===(musicCurrentPath||''));
                  // Re-render cards so load-more button disappears
                  renderMusicCards(currentMusicData);
                }
                // Refresh the Prev/Next label display without mutating positions
                function _refreshMusicNavLabels(){
                  const prevTitleEl=document.getElementById('music-prev-title');
                  const prevTrack=_musicPeekPrev();
                  if(prevTitleEl)prevTitleEl.textContent=prevTrack?prevTrack.name:'';
                  const nextTitleEl=document.getElementById('music-next-title');
                  const nextTrack=_musicPeekNext();
                  if(nextTitleEl)nextTitleEl.textContent=nextTrack?nextTrack.name:'';
                }
                function _musicNextTrack(){
                  if(!musicTrackList.length)return null;
                  if(_musicShuffle){
                    if(!_musicShuffleOrder.length)_buildShuffleOrder();
                    _musicShufflePos=(_musicShufflePos+1)%_musicShuffleOrder.length;
                    const idx=_musicShuffleOrder[_musicShufflePos];
                    return musicTrackList[idx]||null;
                  }
                  const next=musicTrackIndex+1;
                  return next<musicTrackList.length?musicTrackList[next]:null;
                }
                // Peek at the previous/next track WITHOUT advancing position (for label display)
                function _musicPeekPrev(){
                  if(!musicTrackList.length)return null;
                  if(_musicShuffle&&_musicShuffleOrder.length){
                    const pos=_musicShufflePos-1;
                    if(pos<0)return null;
                    return musicTrackList[_musicShuffleOrder[pos]]||null;
                  }
                  const idx=musicTrackIndex-1;
                  return idx>=0?musicTrackList[idx]:null;
                }
                function _musicPeekNext(){
                  if(!musicTrackList.length)return null;
                  if(_musicShuffle&&_musicShuffleOrder.length){
                    const pos=(_musicShufflePos+1)%_musicShuffleOrder.length;
                    // Don't wrap-peek if we're at the last unique position
                    if(_musicShufflePos===_musicShuffleOrder.length-1)return null;
                    return musicTrackList[_musicShuffleOrder[pos]]||null;
                  }
                  const idx=musicTrackIndex+1;
                  return idx<musicTrackList.length?musicTrackList[idx]:null;
                }
                function fmtSec(sec){
                  const s=Math.floor(sec);
                  return Math.floor(s/60)+':'+(s%60).toString().padStart(2,'0');
                }

                let _musicKnownSessionId=null;  // last server session ID seen; null = unknown

                function startMusicPlaybackPoll(){
                  if(musicPlaybackPollTimer)return;
                  musicPlaybackPollTimer=setInterval(async()=>{
                    let s;
                    try{
                      const res=await fetch('/api/music/status');
                      if(!res.ok)return;
                      s=await res.json();
                    }catch(e){ return; }

                    // Detect server restart via the per-process session ID.
                    // If the ID changed (or we didn't have one yet) the server is a fresh process
                    // with no active playback – sync local state and do NOT auto-advance.
                    if(s.sessionId&&s.sessionId!==_musicKnownSessionId){
                      _musicKnownSessionId=s.sessionId;
                      musicIsPlaying=s.isPlaying||s.isPaused;
                      musicCurrentPath=s.currentPath||null;
                      updateMusicBar(s);
                      if(!musicIsPlaying)stopMusicPlaybackPoll();
                      return;
                    }

                    const wasPlaying=musicIsPlaying;
                    musicIsPlaying=s.isPlaying||s.isPaused;
                    updateMusicBar(s);

                    // auto-advance when track ends naturally
                    if(!s.isPlaying&&!s.isPaused&&wasPlaying&&musicCurrentPath&&!s.currentPath){
                      musicCurrentPath=null;
                      if(_musicAutoPlay||_musicShuffle){
                        const nextT=_musicNextTrack();
                        if(nextT){
                          await playMusic(nextT.path,nextT.name);
                          // playMusic restarts the poll; skip the stop-check below
                          return;
                        }
                      }
                    }

                    // Stop polling only when truly idle (nothing playing and we didn't just advance)
                    if(!s.isPlaying&&!s.isPaused&&!s.currentPath&&!musicCurrentPath)stopMusicPlaybackPoll();
                  },1500);
                }

                function stopMusicPlaybackPoll(){
                  if(musicPlaybackPollTimer){clearInterval(musicPlaybackPollTimer);musicPlaybackPollTimer=null;}
                }

                function switchMode(mode){
                  currentMode=mode;
                  // Remove music dock when leaving music mode
                  if(mode!=='music')document.body.classList.remove('music-player-docked');
                  if(mode!=='radio'&&!_radioIsPlaying)document.body.classList.remove('radio-player-docked');
                  document.body.classList.toggle('radio-mode',mode==='radio');
                  ['video','music','radio'].forEach(m=>{
                    const tab=document.getElementById('tab-'+m);
                    const browser=document.getElementById(m==='video'?'browser':m+'-browser');
                    const active=m===mode;
                    if(tab)tab.classList.toggle('active',active);
                    if(browser)browser.style.display=active?'':'none';
                  });
                  // Show/hide the radio sub-tab bar and count in the header
                  const radioTabHdr=document.getElementById('radio-tab-bar-header');
                  const radioCountHdr=document.getElementById('radio-station-count');
                  if(radioTabHdr)radioTabHdr.style.display=(mode==='radio'?'flex':'none');
                  if(radioCountHdr)radioCountHdr.style.display=(mode==='radio'?'':'none');
                  const searchEl=document.getElementById('search');
                  const searchRow=document.getElementById('search-row');
                  if(mode==='video'){
                    if(searchRow)searchRow.style.display='';
                    searchEl.placeholder='Search entire library...';
                    stopMusicStatusPoll();
                    stopRadioStatusPoll();
                    const back=document.getElementById('back-button');
                    if(back)back.onclick=goBack;
                    refreshLibraryStatus();
                    if(currentData)render(currentData);
                    else browse(null);
                  }else if(mode==='music'){
                    if(searchRow)searchRow.style.display='';
                    searchEl.placeholder='Search music library...';
                    stopRadioStatusPoll();
                    document.body.classList.remove('radio-player-docked');
                    // clear any video scan-status left-overs
                    const el=document.getElementById('scan-status');
                    if(el){el.textContent='';el.className='';}
                    // restore music bar if already playing
                    if(musicIsPlaying)startMusicPlaybackPoll();
                    if(!currentMusicData)browseMusic(null);
                    else renderMusicHeader(currentMusicData,false);
                  }else if(mode==='radio'){
                    if(searchRow)searchRow.style.display='none';
                    stopMusicStatusPoll();
                    setMusicHeaderForMode('radio');
                    radioInit();
                  }
                }

                function setMusicHeaderForMode(mode){
                  const bc=document.getElementById('breadcrumb');
                  const countLine=document.getElementById('count-line');
                  const scanStatus=document.getElementById('scan-status');
                  const back=document.getElementById('back-button');
                  if(back)back.style.display='none';
                  if(bc)bc.innerHTML='';
                  if(countLine)countLine.textContent='';
                  if(scanStatus){scanStatus.textContent='';scanStatus.className='';}
                }

                function renderMusicHeader(data,searching){
                  const bc=document.getElementById('breadcrumb');
                  const countLine=document.getElementById('count-line');
                  const scanStatus=document.getElementById('scan-status');
                  const back=document.getElementById('back-button');

                  // Breadcrumb
                  if(bc){
                    bc.innerHTML='';
                    if(searching){
                      bc.innerHTML='<a onclick="browseMusic(null)">&#128193; Music Root</a><span> &rsaquo; </span><span class="crumb-current">Search results</span>';
                    }else{
                      const root=data.musicRoot||'';
                      const folder=data.folder||root;
                      let html='<a onclick="browseMusic(null)">&#128193; Music Root</a>';
                      if(folder&&root&&folder.toLowerCase()!==root.toLowerCase()){
                        // Build segments relative to music root
                        const rel=folder.startsWith(root)?folder.slice(root.length):'';
                        const sep=rel.indexOf('/')!==-1?'/':'\\';
                        const parts=rel.split(sep).filter(p=>p.length>0);
                        let cumulative=root;
                        parts.forEach((part,i)=>{
                          cumulative+=sep+part;
                          const captured=cumulative;
                          if(i===parts.length-1){
                            html+='<span> &rsaquo; </span><span class="crumb-current">'+esc(part)+'</span>';
                          }else{
                            html+='<span> &rsaquo; </span><a onclick="browseMusic(\''+jsStr(captured)+'\')">'+esc(part)+'</a>';
                          }
                        });
                      }
                      bc.innerHTML=html;
                    }
                  }

                  // Back button
                  if(back){
                    const canGoBack=searching||(musicBrowseHistory.length>0)||(data.folder!=null&&data.folder!==(data.musicRoot||''));
                    back.style.display=canGoBack?'block':'none';
                    back.dataset.dir='';
                    back.onclick=()=>{
                      if(currentMode==='music'){
                        if(searching){browseMusic(currentMusicFolder);return;}
                        const prev=musicBrowseHistory.length>0?musicBrowseHistory.pop():null;
                        // Navigate directly without re-pushing to history
                        currentMusicFolder=prev;
                        const mb=document.getElementById('music-browser');
                        if(mb)mb.innerHTML='<div style="padding:.75rem;color:var(--muted,#9aa8c2)">Loading\u2026</div>';
                        const url='/api/music/browse'+(prev?'?folder='+encodeURIComponent(prev):'');
                        fetch(url).then(r=>r.ok?r.json():null).then(data=>{
                          if(!data)return;
                          currentMusicData=data;currentMusicData.folder=prev;
                          if(currentMode==='music')renderMusicHeader(currentMusicData,false);
                          renderMusicCards(currentMusicData);
                        }).catch(()=>{});
                      }else goBack();
                    };
                  }

                  // Count line
                  if(countLine){
                    if(searching){
                      const fc=data.folders?.length||0;
                      const tc=data.files?.length||0;
                      countLine.textContent=fc+' folder(s) and '+tc+' result(s)';
                    }else{
                      const fc=data.folders?.length||0;
                      const loaded=data.files?.length||0;
                      const total=data.totalInFolder||loaded;
                      if(total>loaded){
                        countLine.textContent=fc+' folder(s), '+loaded+' of '+total+' track(s) loaded';
                      }else{
                        countLine.textContent=fc+' folder(s), '+total+' track(s)';
                      }
                    }
                  }

                  // Scan status line
                  if(scanStatus&&currentMode==='music'){
                    scanStatus.classList.remove('scanning','error','global-scan-status');
                    const total=Number(data.indexedFiles)||0;
                    const isScanning=Boolean(data.indexing);
                    const err=(data.lastError||'').trim();
                    const root=(data.musicRoot||'').trim();
                    if(err){
                      scanStatus.classList.add('error');
                      scanStatus.textContent='Music scan failed: '+err+(root?' (path: '+root+')':'');
                    }else if(isScanning){
                      scanStatus.classList.add('scanning','global-scan-status');
                      scanStatus.textContent='Scanning music library\u2026 '+total+' track(s) indexed so far.';
                      startMusicStatusPoll();
                    }else if(total>0){
                      scanStatus.textContent='Music Library Ready: '+total+' song(s)'+(root?' \u2014 '+root:'');
                    }else{
                      scanStatus.textContent='Music library empty or path not configured'+(root?' \u2014 path: '+root:'');
                    }
                  }
                }

                function startMusicStatusPoll(){
                  if(musicStatusPollTimer)return;
                  let pollCount=0;
                  musicStatusPollTimer=setInterval(async()=>{
                    try{
                      const res=await fetch('/api/music/status');
                      if(!res.ok)return;
                      const s=await res.json();
                      if(currentMode!=='music')return stopMusicStatusPoll();
                      if(!s.isScanning){
                        stopMusicStatusPoll();
                        browseMusic(currentMusicFolder);
                      }else{
                        // Update live count directly in the scan status element
                        const scanStatus=document.getElementById('scan-status');
                        if(scanStatus&&currentMode==='music'){
                          scanStatus.classList.add('scanning','global-scan-status');
                          const folder=(s.currentScanFolder||'').replace(/.*[/\\]/,'');
                          const folderHint=folder?' \u2014 '+folder:'';
                          scanStatus.textContent='Scanning music library\u2026 '+(s.indexedFiles||0)+' track(s) indexed'+folderHint;
                        }
                        if(currentMusicData){
                          currentMusicData.indexing=true;
                          currentMusicData.indexedFiles=s.indexedFiles||0;
                        }
                        // Re-browse every ~10s during scan so tracks appear progressively
                        pollCount++;
                        if(pollCount%5===0)browseMusic(currentMusicFolder,0,false);
                      }
                    }catch(e){}
                  },2000);
                }

                function stopMusicStatusPoll(){
                  if(musicStatusPollTimer){clearInterval(musicStatusPollTimer);musicStatusPollTimer=null;}
                }

                async function browseMusic(folder,offset=0,append=false){
                  if(!append){
                    // Push current folder to history before navigating
                    if(currentMusicFolder!==folder){
                      if(currentMusicFolder!==null) musicBrowseHistory.push(currentMusicFolder);
                      // Going to root resets history
                      if(folder===null||folder===undefined) musicBrowseHistory=[];
                    }
                    currentMusicFolder=folder;
                  }
                  const mb=document.getElementById('music-browser');
                  if(!mb)return;
                  if(!append)mb.innerHTML='<div style="padding:.75rem;color:var(--muted,#9aa8c2)">Loading…</div>';
                  try{
                    const url='/api/music/browse'+(folder?'?folder='+encodeURIComponent(folder):'')+(offset?'&offset='+encodeURIComponent(offset):'');
                    const res=await fetch(url);
                    if(!res.ok){mb.innerHTML='<div style="padding:.75rem">Error '+res.status+'</div>';return;}
                    const data=await res.json();
                    if(append&&currentMusicData){
                      currentMusicData.files=[...(currentMusicData.files||[]),...(data.files||[])];
                      currentMusicData.offset=data.offset;
                      currentMusicData.hasMore=data.hasMore;
                      // totalInFolder comes from the first page; don't overwrite it
                      if(data.totalInFolder)currentMusicData.totalInFolder=data.totalInFolder;
                    }else{
                      currentMusicData=data;
                      currentMusicData.folder=folder;
                    }
                    if(currentMode==='music')renderMusicHeader(currentMusicData,false);
                    renderMusicCards(currentMusicData);
                  }catch(e){mb.innerHTML='<div style="padding:.75rem">Error: '+e+'</div>';}
                }

                async function searchMusicLibrary(q,offset=0,append=false){
                  const mb=document.getElementById('music-browser');
                  if(!mb)return;
                  try{
                    const res=await fetch('/api/music/search?q='+encodeURIComponent(q)+(offset?'&offset='+encodeURIComponent(offset):''));
                    if(!res.ok){setSearchBusy(false);return;}
                    const data=await res.json();
                    if(append&&currentMusicData){
                      currentMusicData.files=[...(currentMusicData.files||[]),...(data.files||[])];
                      currentMusicData.hasMore=data.hasMore;
                    }else currentMusicData={...data,folders:[],query:q};
                    setSearchBusy(false);
                    if(currentMode==='music')renderMusicHeader(currentMusicData,true);
                    renderMusicCards(currentMusicData,true);
                  }catch(e){setSearchBusy(false);}
                }

                function renderMusicCards(data,searching){
                  const mb=document.getElementById('music-browser');
                  if(!mb)return;
                  let html='';
                  if(!searching&&data.folders&&data.folders.length){
                    html+='<div class="folder-list">';
                    data.folders.forEach(f=>{
                      html+='<div class="folder-row" role="button" tabindex="0" onkeydown="activateKeyboardClick(event,this)" onclick="browseMusic(\''+jsStr(f.folder)+'\')">'
                          +'<span class="folder-icon">&#128193;</span><span class="folder-name">'+esc(f.name)+'</span></div>';
                    });
                    html+='</div>';
                  }
                  if(data.files&&data.files.length){
                    html+='<div class="section-label">Tracks</div><div class="music-grid">';
                    data.files.forEach(f=>{
                      const isPlaying=musicCurrentPath&&f.path===musicCurrentPath;
                      html+='<div class="music-track-card'+(isPlaying?' playing':'')+'" data-path="'+esc(f.path)+'" onclick="playMusic(\''+jsStr(f.path)+'\',\''+jsStr(f.name)+'\')">'  
                          +'<div class="track-name">'+esc(f.name)+'</div>'
                          +'<div class="track-folder">'+esc(f.folder)+'</div></div>';
                    });
                    html+='</div>';
                    if(data.hasMore){
                      const shown=data.files.length;
                      const total=data.totalInFolder||shown;
                      html+='<button class="load-more-btn" onclick="loadMoreMusic()">Load more tracks ('+shown+' of '+total+' loaded)</button>';
                    }
                  }else if(data.folder&&data.indexing){
                    html+='<div style="padding:1rem;color:var(--muted,#9aa8c2)">&#128197; Still indexing \u2014 tracks will appear here when the scan reaches this folder.</div>';
                  }else if(data.folder&&!data.indexing){
                    html+='<div style="padding:1rem;color:var(--muted,#9aa8c2)">No tracks found.</div>';
                  }
                  mb.innerHTML=html||'<div style="padding:1rem;color:var(--muted,#9aa8c2)">Select a folder to browse tracks.</div>';
                }

                function loadMoreMusic(){
                  if(!currentMusicData)return;
                  const offset=(currentMusicData.files||[]).length;
                  if(currentMusicData.query!==undefined)
                    searchMusicLibrary(currentMusicData.query||'',offset,true);
                  else
                    browseMusic(currentMusicData.folder||currentMusicFolder,offset,true);
                }

                // kept for compatibility — routes to the card renderer
                function renderMusic(data,searching){renderMusicCards(data,searching);}

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
          const canGoBack=!searching&&(browseHistory.length>0||(data.parent!=null&&!data.isRoot));
          back.style.display=canGoBack?'block':'none';
          back.dataset.dir=(!data.isRoot&&data.parent!=null&&!searching)?data.parent:'';
          bc.innerHTML='';
          if(searching){bc.innerHTML+='<a onclick="browse(null)">&#8962; Root</a><span> &rsaquo; </span><span class="crumb-current">Search results</span>';}
          else if(data.breadcrumbs&&data.breadcrumbs.length){
            bc.innerHTML+=data.breadcrumbs.map((c,i)=>{
              const sep=i>0?'<span> &rsaquo; </span>':'';
              const label=i===0?'&#8962; Root':esc(c.name);
              const isLast=i===data.breadcrumbs.length-1;
              const cls=isLast?' class="crumb-current"':'';
              const linkBadge=isLast&&currentIsLinkedDir?' <span class="folder-link-badge" title="Navigated via library link">\uD83D\uDD17</span>':'';
              return sep+'<a'+cls+' onclick="browse(\''+c.dir+'\')">'+(isLast?label+linkBadge:label)+'</a>';
            }).join('');
          }
          if(searching)countLine.textContent=(data.folders?.length||0)+' folder(s) and '+data.files.length+' result(s)';
          else countLine.textContent=data.folders.length+' folder(s), '+data.files.length+' video(s)';
          let html='';
          if(data.folders.length){
            if(searching){
              const folderLabel=data.folders.length>5?'Folders ('+data.folders.length+', scroll for more)':'Folders';
              html+='<div class="section-label">'+folderLabel+'</div>';
              html+='<div class="folder-list search-folders">';
              html+=data.folders.map(f=>'<div class="folder-row" role="button" tabindex="0" onkeydown="activateKeyboardClick(event,this)" onclick="this.classList.add(\'folder-row-active\');searchLibrary(\''+esc(f.folder||f.name)+'\')">'+'<span class="folder-icon">&#128193;</span><span class="folder-name">'+esc(f.name||f.folder)+'</span></div>').join('');
              html+='</div>';
            }else{
              html+='<div class="folder-list">';
              html+=data.folders.map(f=>'<div class="folder-row" role="button" tabindex="0" onkeydown="activateKeyboardClick(event,this)" onclick="this.classList.add(\'folder-row-active\');browse(\''+f.dir+'\',0,false,true,'+(f.isLink?'true':'false')+')">'+'<span class="folder-icon">&#128193;</span><span class="folder-name">'+esc(f.name)+'</span>'+(f.isLink?'<span class="folder-link-badge" title="Library link">🔗</span>':'')+'</div>').join('');
              html+='</div>';
            }
          }
          if(data.files.length){
            html+='<div class="section-label">Videos</div><div class="movie-grid">';
            html+=data.files.map(f=>{
              if(f.watched&&!playedVideos.has(f.path)){playedVideos.add(f.path);savePlayedVideos();}
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
              const linkBadge=f.isLink?'<div class="queue-badge link-badge" title="Library link">🔗</div>':'';
              return '<div class="'+cardClass+'" id="'+cardIdFor(f.path)+'" data-path="'+esc(f.path)+'" role="button" tabindex="0" aria-label="Play '+esc(displayName)+'" '+bg+action+' onkeydown="activateKeyboardClick(event,this)" onpointerdown="beginCardHold(event,\''+f.path+'\')" onpointerup="endCardHold()" onpointercancel="endCardHold()" onpointerleave="endCardHold()">'+
                '<div class="movie-card-inner">'+
                '<div class="movie-title">'+esc(displayName)+'</div>'+
                queuedBadge+
                favoriteBadge+
                linkBadge+
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
          if(f.watched&&!playedVideos.has(f.path)){playedVideos.add(f.path);savePlayedVideos();}
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
        function goBack(){
          currentIsLinkedDir=false;
          if(browseHistory.length>0){
            const prev=browseHistory.pop();
            browse(prev,0,false,false);
          }else{
            const d=document.getElementById('back-button').dataset.dir;
            if(d)browse(d,0,false,false);
          }
        }
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
          api('/api/history/watched/set?path='+encodeURIComponent(p)+'&watched=true');
          for(const card of getMovieCards(p)){
            card.classList.add('played');
            const watchedButton=findWatchedButton(card);
            if(watchedButton)watchedButton.textContent='Unwatch';
          }
        }
        function unmarkPlayed(p){
          playedVideos.delete(p);savePlayedVideos();
          api('/api/history/watched/set?path='+encodeURIComponent(p)+'&watched=false');
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
        // Escape a value for embedding inside a single-quoted JS string literal in an HTML attribute.
        function jsStr(s){return String(s).replace(/\\/g,'\\\\').replace(/'/g,"\\'");}
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

        // ── Radio ────────────────────────────────────────────────────────────
        let _radioInited=false;
        let _radioStations=[];
        let _radioFavorites=[];
        let _radioFavCountries=[]; // country codes marked as favorites (localStorage)
        let _radioFavTags=[]; // genre tags marked as favorites (localStorage)
        let _radioCurrentUrl='';
        let _radioCurrentName='';
        let _radioCurrentCountry='';
        let _radioCurrentTag='';
        let _radioCurrentStation=null; // full station object for player display
        let _radioIsPlaying=false;
        let _radioStatusPoll=null;
        let _radioCountries=[];
        let _radioTags=[];
        let _radioTab='top'; // 'top'|'favorites'
        let _radioPage=0; // current page (0-based), each page = 80 stations
        const _radioPageSize=80;
        let _radioFilterQ='';
        let _radioFilterCountry='';
        let _radioFilterTag='';
        let _radioSortBy='votes'; // 'votes'|'name'|'bitrate'
        let _radioMinBitrate=0; // 0=all, else minimum kbps

        const _radioFavCountriesKey='rp_radio_fav_countries';
        const _radioFavTagsKey='rp_radio_fav_tags';
        function radioLoadFavCountries(){
          try{_radioFavCountries=JSON.parse(localStorage.getItem(_radioFavCountriesKey)||'[]');}catch{_radioFavCountries=[];}
          try{_radioFavTags=JSON.parse(localStorage.getItem(_radioFavTagsKey)||'[]');}catch{_radioFavTags=[];}
        }
        function radioSaveFavCountries(){
          try{localStorage.setItem(_radioFavCountriesKey,JSON.stringify(_radioFavCountries));}catch{}
        }
        function radioSaveFavTags(){
          try{localStorage.setItem(_radioFavTagsKey,JSON.stringify(_radioFavTags));}catch{}
        }
        function radioIsCountryFav(code){return _radioFavCountries.includes(code);}
        function radioIsTagFav(tag){return _radioFavTags.includes(tag);}
        function radioToggleCountryFav(code,evt){
          if(evt){evt.stopPropagation();}
          if(!code)return;
          const i=_radioFavCountries.indexOf(code);
          if(i>=0)_radioFavCountries.splice(i,1); else _radioFavCountries.push(code);
          radioSaveFavCountries();
          _rebuildSelectOptions('radio-country',_radioCountries.map(c=>({value:c.code,label:c.name})),_radioFavCountries,'radioToggleCountryFav');
          radioFetch();
        }
        function radioToggleTagFav(tag,evt){
          if(evt){evt.stopPropagation();}
          if(!tag)return;
          const i=_radioFavTags.indexOf(tag);
          if(i>=0)_radioFavTags.splice(i,1); else _radioFavTags.push(tag);
          radioSaveFavTags();
          _rebuildSelectOptions('radio-tag',_radioTags.map(t=>({value:t,label:t})),_radioFavTags,'radioToggleTagFav');
          radioFetch();
        }

        // Build a custom-styled select wrapper with heart-toggle buttons per option.
        // Renders as a native <select> for value, with an overlay list for the hearts.
        // Build a native <select> with favorites grouped at top + divider,
        // plus a heart button beside it to toggle the currently selected value as a favorite.
        function _buildFavSelect(id,items,favList,toggleFn,allLabel){
          const favSet=new Set(favList);
          const favItems=[...items].filter(it=>favSet.has(it.value)).sort((a,b)=>a.label.localeCompare(b.label));
          const restItems=[...items].filter(it=>!favSet.has(it.value)).sort((a,b)=>a.label.localeCompare(b.label));
          let h=`<div class="radio-fav-select-wrap"><select id="${id}" onchange="${id==='radio-country'?'radioOnCountryChange()':"_syncFavSelectHeart('"+id+"','"+toggleFn+"')"}">`;
          h+=`<option value="">${allLabel}</option>`;
          if(favItems.length&&restItems.length){
            h+='<optgroup label="\u2764 Favorites">';
            for(const it of favItems)h+=`<option value="${escHtml(it.value)}">${escHtml(it.label)}</option>`;
            h+='</optgroup><optgroup label="\u2500\u2500\u2500\u2500\u2500">';
            for(const it of restItems)h+=`<option value="${escHtml(it.value)}">${escHtml(it.label)}</option>`;
            h+='</optgroup>';
          }else{
            for(const it of [...favItems,...restItems])h+=`<option value="${escHtml(it.value)}">${escHtml(it.label)}</option>`;
          }
          h+='</select>';
          h+=`<button class="radio-fav-select-heart-inline" id="${id}-heart" onclick="${toggleFn}(document.getElementById('${id}').value,event)" title="Toggle as favorite">\u2665</button>`;
          h+='</div>';
          return h;
        }

        function _syncFavSelectHeart(selectId,toggleFn){
          _radioPage=0;
          radioFetch();
          const sel=document.getElementById(selectId);
          const btn=document.getElementById(selectId+'-heart');
          if(!sel||!btn)return;
          const isCountry=(toggleFn==='radioToggleCountryFav');
          const isFav=sel.value?(isCountry?radioIsCountryFav(sel.value):radioIsTagFav(sel.value)):false;
          btn.classList.toggle('active',isFav);
        }

        async function radioOnCountryChange(){
          const cc=(document.getElementById('radio-country')||{}).value||'';
          const url='/api/radio/tags'+(cc?'?country='+encodeURIComponent(cc):'');
          try{
            const r=await fetch(url);
            if(r.ok){
              const newTags=await r.json();
              const prevTag=(document.getElementById('radio-tag')||{}).value||'';
              _radioTags=newTags;
              _rebuildSelectOptions('radio-tag',_radioTags.map(t=>({value:t,label:t})),_radioFavTags,'radioToggleTagFav');
              const tagSel=document.getElementById('radio-tag');
              if(tagSel)tagSel.value=newTags.includes(prevTag)?prevTag:'';
            }
          }catch(e){console.warn('tags refresh failed',e);}
          _syncFavSelectHeart('radio-country','radioToggleCountryFav');
        }

        // Rebuild just the options of an existing fav select after a heart toggle.
        function _rebuildSelectOptions(id,items,favList,toggleFn){
          const sel=document.getElementById(id);
          if(!sel)return;
          const curVal=sel.value;
          const favSet=new Set(favList);
          const favItems=[...items].filter(it=>favSet.has(it.value)).sort((a,b)=>a.label.localeCompare(b.label));
          const restItems=[...items].filter(it=>!favSet.has(it.value)).sort((a,b)=>a.label.localeCompare(b.label));
          const allLabel=sel.options[0]?sel.options[0].text:'All';
          sel.innerHTML=`<option value="">${allLabel}</option>`;
          if(favItems.length&&restItems.length){
            let og=document.createElement('optgroup');og.label='\u2764 Favorites';
            for(const it of favItems){const o=document.createElement('option');o.value=it.value;o.textContent=it.label;og.appendChild(o);}
            sel.appendChild(og);
            let og2=document.createElement('optgroup');og2.label='\u2500\u2500\u2500\u2500\u2500';
            for(const it of restItems){const o=document.createElement('option');o.value=it.value;o.textContent=it.label;og2.appendChild(o);}
            sel.appendChild(og2);
          }else{
            const all=[...favItems,...restItems];
            for(const it of all){const o=document.createElement('option');o.value=it.value;o.textContent=it.label;sel.appendChild(o);}
          }
          sel.value=curVal;
          const btn=document.getElementById(id+'-heart');
          if(btn)btn.classList.toggle('active',!!curVal&&new Set(favList).has(curVal));
        }

        async function radioInit(){
          const rb=document.getElementById('radio-browser');
          if(!rb)return;
          rb.innerHTML='<div style="color:var(--muted,#9aa8c2);padding:.5rem">Loading stations\u2026</div>';
          radioLoadFavCountries();
          // Load countries/tags in parallel
          if(!_radioCountries.length||!_radioTags.length){
            try{
              const [cr,tr]=await Promise.all([fetch('/api/radio/countries').then(r=>r.json()),fetch('/api/radio/tags').then(r=>r.json())]);
              _radioCountries=cr||[];
              _radioTags=tr||[];
            }catch{}
          }
          await radioLoadFavorites();
          await radioShowTab(_radioTab);
          startRadioStatusPoll();
          _radioInited=true;
        }

        async function radioLoadFavorites(){
          try{
            const r=await fetch('/api/radio/favorites');
            _radioFavorites=r.ok?await r.json():[];
          }catch{_radioFavorites=[];}
        }

        async function radioShowTab(tab){
          // Persist current filter values before rebuilding the DOM
          const prevQ=_radioFilterQ;
          const prevCountry=_radioFilterCountry;
          const prevTag=_radioFilterTag;
          const sameTab=(tab===_radioTab);
          _radioTab=tab;
          _radioPage=0;
          const rb=document.getElementById('radio-browser');
          if(!rb)return;
          rb.style.display='';
          let html='';
          // Sync header tab buttons active state
          const hts=document.getElementById('header-tab-stations');
          const htf=document.getElementById('header-tab-favorites');
          if(hts)hts.classList.toggle('active',tab==='top');
          if(htf)htf.classList.toggle('active',tab==='favorites');
          // Filters (only for top/search)
          if(tab!=='favorites'){
            html+='<div class="radio-filter-row" id="radio-filters">';
            html+='<input id="radio-search-box" type="search" placeholder="Station name\u2026" style="background:var(--input-bg);color:var(--input-text);border:1px solid var(--input-border);padding:.55rem .6rem;border-radius:4px;font-size:.9rem;min-width:143px;width:208px;min-height:2.2rem" oninput="radioOnSearchInput()" />';
            html+=_buildFavSelect('radio-country',_radioCountries.map(c=>({value:c.code,label:c.name})),_radioFavCountries,'radioToggleCountryFav','All countries');
            html+=_buildFavSelect('radio-tag',_radioTags.map(t=>({value:t,label:t})),_radioFavTags,'radioToggleTagFav','All genres');
            html+=`<select id="radio-sort" class="radio-sort-select" onchange="radioOnSortChange()" title="Sort by" aria-label="Sort stations by">
              <option value="votes"${_radioSortBy==='votes'?' selected':''}>↑ Votes</option>
              <option value="name"${_radioSortBy==='name'?' selected':''}>A–Z Name</option>
              <option value="bitrate"${_radioSortBy==='bitrate'?' selected':''}>↑ Bitrate</option>
            </select>`;
            html+=`<select id="radio-minbitrate" class="radio-bitrate-select" onchange="radioOnBitrateChange()" title="Min bitrate" aria-label="Minimum bitrate">
              <option value="0"${_radioMinBitrate===0?' selected':''}>Any kbps</option>
              <option value="64"${_radioMinBitrate===64?' selected':''}>64+ kbps</option>
              <option value="128"${_radioMinBitrate===128?' selected':''}>128+ kbps</option>
              <option value="192"${_radioMinBitrate===192?' selected':''}>192+ kbps</option>
              <option value="320"${_radioMinBitrate===320?' selected':''}>320+ kbps</option>
            </select>`;
            html+='<button class="radio-reset-btn" onclick="radioResetFilters()" title="Reset all filters">&#10005; Reset</button>';
            html+='</div>';
          }
          html+='<div id="radio-cards"></div>';
          rb.innerHTML=html;
          // Restore filter state
          if(tab!=='favorites'){
            const sb=document.getElementById('radio-search-box');
            const sc=document.getElementById('radio-country');
            const st=document.getElementById('radio-tag');
            if(sb)sb.value=prevQ;
            if(sc)sc.value=prevCountry;
            if(st)st.value=prevTag;
            // Sync heart states after restoring selection
            const bhc=document.getElementById('radio-country-heart');
            if(bhc)bhc.classList.toggle('active',!!prevCountry&&radioIsCountryFav(prevCountry));
            const bht=document.getElementById('radio-tag-heart');
            if(bht)bht.classList.toggle('active',!!prevTag&&radioIsTagFav(prevTag));
          }
          if(tab==='favorites'){
            renderRadioCards(_radioFavorites,false);
          }else{
            await radioFetch();
          }
        }

        function radioOnSortChange(){
          const sel=document.getElementById('radio-sort');
          if(sel)_radioSortBy=sel.value;
          renderRadioCards(_radioStations,_radioStations.length===_radioPageSize*(_radioPage+1));
        }
        function radioOnBitrateChange(){
          const sel=document.getElementById('radio-minbitrate');
          if(sel)_radioMinBitrate=parseInt(sel.value)||0;
          renderRadioCards(_radioStations,_radioStations.length===_radioPageSize*(_radioPage+1));
        }

        let _radioSearchTimer=null;
        function radioOnSearchInput(){
          clearTimeout(_radioSearchTimer);
          _radioPage=0;
          _radioSearchTimer=setTimeout(()=>radioFetch(),420);
        }

        function radioResetFilters(){
          const sb=document.getElementById('radio-search-box');
          const sc=document.getElementById('radio-country');
          const st=document.getElementById('radio-tag');
          const ss=document.getElementById('radio-sort');
          const sb2=document.getElementById('radio-minbitrate');
          if(sb)sb.value='';
          if(sc)sc.value='';
          if(st)st.value='';
          if(ss)ss.value='votes';
          if(sb2)sb2.value='0';
          _radioFilterQ='';_radioFilterCountry='';_radioFilterTag='';
          _radioSortBy='votes';_radioMinBitrate=0;
          const bhc=document.getElementById('radio-country-heart');
          if(bhc)bhc.classList.remove('active');
          const bht=document.getElementById('radio-tag-heart');
          if(bht)bht.classList.remove('active');
          _radioPage=0;
          radioFetch();
        }

        async function radioFetch(append=false){
          const tab=_radioTab;
          const q=(document.getElementById('radio-search-box')||{}).value||'';
          const country=(document.getElementById('radio-country')||{}).value||'';
          const tag=(document.getElementById('radio-tag')||{}).value||'';
          // Persist filter state so switching tabs and back restores them
          _radioFilterQ=q;
          _radioFilterCountry=country;
          _radioFilterTag=tag;
          if(!append)_radioPage=0;
          const offset=_radioPage*_radioPageSize;
          const cards=document.getElementById('radio-cards');
          if(cards&&!append)cards.innerHTML='<div style="color:var(--muted,#9aa8c2);padding:.4rem">Loading\u2026</div>';
          try{
            let newStations;
            if(!q&&!country&&!tag&&tab==='top'){
              const r=await fetch(`/api/radio/top?limit=${_radioPageSize}&offset=${offset}`);
              newStations=r.ok?await r.json():[];
            }else{
              const params=new URLSearchParams({q,country,tag,limit:String(_radioPageSize),offset:String(offset)});
              const r=await fetch('/api/radio/search?'+params);
              newStations=r.ok?await r.json():[];
            }
            if(append){
              _radioStations=[..._radioStations,...newStations];
            }else{
              _radioStations=newStations;
            }
            renderRadioCards(_radioStations,newStations.length===_radioPageSize);
          }catch{
            if(cards&&!append)cards.innerHTML='<div style="color:#ff7777;padding:.4rem">Failed to load stations.</div>';
          }
        }

        async function radioLoadMore(){
          _radioPage++;
          await radioFetch(true);
        }

        function radioIsFav(uuid){
          return _radioFavorites.some(f=>(f.stationuuid||f.uuid||f.Uuid)===uuid);
        }

        function _sortStationsAlpha(arr){
          return [...arr].sort((a,b)=>{
            const na=(a.name||a.Name||'').toLowerCase();
            const nb=(b.name||b.Name||'').toLowerCase();
            return na<nb?-1:na>nb?1:0;
          });
        }

        function _sortAndFilterStations(arr){
          let list=arr;
          // Bitrate filter
          if(_radioMinBitrate>0)list=list.filter(s=>(s.bitrate||s.Bitrate||0)>=_radioMinBitrate);
          // Sort
          if(_radioSortBy==='name'){
            list=[...list].sort((a,b)=>{
              const na=(a.name||a.Name||'').toLowerCase();
              const nb=(b.name||b.Name||'').toLowerCase();
              return na<nb?-1:na>nb?1:0;
            });
          }else if(_radioSortBy==='bitrate'){
            list=[...list].sort((a,b)=>((b.bitrate||b.Bitrate||0)-(a.bitrate||a.Bitrate||0)));
          }else{
            // votes (default)
            list=[...list].sort((a,b)=>((b.votes||b.Votes||0)-(a.votes||a.Votes||0)));
          }
          return list;
        }

        function _buildStationCard(s){
          const uuid=s.stationuuid||s.uuid||s.Uuid||'';
          const name=s.name||s.Name||'';
          const url=s.streamUrl||s.StreamUrl||s.url_resolved||'';
          const country=s.country||s.Country||'';
          const state=s.state||s.State||'';
          const language=s.language||s.Language||'';
          const tags=(s.tags||s.Tags||'').split(',').filter(Boolean).slice(0,4).join(', ');
          const bitrate=s.bitrate||s.Bitrate||0;
          const codec=(s.codec||s.Codec||'').toUpperCase();
          const votes=s.votes||s.Votes||0;
          const clicks=s.clickcount||s.ClickCount||0;
          const hls=s.hls||s.Hls||0;
          const homepage=s.homepage||s.Homepage||'';
          // location line: country + state
          const location=[country,state].filter(Boolean).join(' – ');
          // tech line: codec + bitrate + HLS badge
          const techParts=[codec,bitrate?bitrate+'kbps':'',hls?'HLS':''].filter(Boolean);
          const tech=techParts.join(' · ');
          // popularity: votes + clicks
          const pop=[votes?'▲ '+votes.toLocaleString():'',clicks?'▶ '+clicks.toLocaleString():''].filter(Boolean).join('  ');
          const favIcon=radioIsFav(uuid)?'&#10084;':'&#9825;';
          const isPlaying=_radioCurrentUrl&&url&&_radioCurrentUrl===url;
          const stationJson=encodeURIComponent(JSON.stringify({stationuuid:uuid,name,url_resolved:url,country,countrycode:s.countryCode||s.CountryCode||s.countrycode||'',state:s.state||s.State||'',language,tags:s.tags||s.Tags||'',codec,bitrate,votes,clickcount:clicks,hls,favicon:s.favicon||s.Favicon||'',homepage,geo_lat:s.geo_lat??s.GeoLat??null,geo_long:s.geo_long??s.GeoLong??null}));
          const encCountry=encodeURIComponent(country);
          const encTagFirst=encodeURIComponent((s.tags||s.Tags||'').split(',').filter(Boolean)[0]||'');
          let h=`<div class="radio-station-card${isPlaying?' playing':''}" data-url="${escHtml(url)}" onclick="radioPlayStation('${encodeURIComponent(url)}','${encodeURIComponent(name)}','${encCountry}','${encTagFirst}','${stationJson}')">`;
          // header row: favicon + name + quality badge + fav button
          h+='<div style="display:flex;gap:.4rem;align-items:flex-start">';
          if(s.favicon||s.Favicon)h+=`<img src="${escHtml(s.favicon||s.Favicon||'')}" alt="" style="width:24px;height:24px;border-radius:3px;object-fit:contain;flex-shrink:0;margin-top:.1rem" onerror="this.style.display='none'" />`;
          h+=`<span class="station-name" style="flex:1">${escHtml(name)}</span>`;
          // Quality badge (codec + bitrate)
          const badgeParts=[codec,bitrate?bitrate+'k':''].filter(Boolean);
          if(badgeParts.length)h+=`<span class="radio-quality-badge">${escHtml(badgeParts.join(' '))}</span>`;
          h+=`<button class="radio-fav-btn${radioIsFav(uuid)?' active':''}" onclick="event.stopPropagation();radioToggleFav(this,'${stationJson}')" title="Favorite">${favIcon}</button>`;
          h+='</div>';
          // location + tags
          if(location||tags)h+=`<div class="station-meta">${escHtml([location,tags].filter(Boolean).join(' · '))}</div>`;
          // tech + language + popularity
          const detailParts=[tech,language?'🗣️ '+language:'',pop].filter(Boolean);
          if(detailParts.length)h+=`<div class="station-detail">${escHtml(detailParts.join('  ·  '))}</div>`;
          // homepage link
          if(homepage)h+=`<div style="margin-top:.2rem"><a href="${escHtml(homepage)}" target="_blank" rel="noopener" class="station-homepage" onclick="event.stopPropagation()">${escHtml(homepage.replace(/^https?:\/\//,'').split('/')[0])}</a></div>`;
          h+='</div>';
          return h;
        }

        function renderRadioCards(stations,hasMore=false){
          const cards=document.getElementById('radio-cards');
          if(!cards)return;
          if(!stations.length){
            const countEl=document.getElementById('radio-station-count');
            if(countEl)countEl.textContent='0 stations';
            cards.innerHTML='<div style="color:var(--muted,#9aa8c2);padding:.5rem">No stations found.</div>';
            return;
          }
          const favCodes=new Set(_radioFavCountries.map(c=>c.toUpperCase()));
          const pinned=_sortStationsAlpha(stations.filter(s=>{const cc=(s.countryCode||s.CountryCode||'').toUpperCase();return favCodes.has(cc);}));
          const rest=_sortStationsAlpha(stations.filter(s=>{const cc=(s.countryCode||s.CountryCode||'').toUpperCase();return !favCodes.has(cc);}));
          let html='';
          const countEl=document.getElementById('radio-station-count');
          if(countEl){
            if(hasMore){
              countEl.innerHTML=`${stations.length} loaded \u00b7 <a href="#" class="count-load-more" onclick="event.preventDefault();radioLoadMore()">more available</a>`;
              countEl.title='Radio Browser API does not provide total counts \u2014 use filters to narrow results';
            }else{
              countEl.textContent=`${stations.length} station${stations.length===1?'':'s'}`;
              countEl.title='';
            }
          }
          if(pinned.length&&rest.length){
            html+='<div class="radio-section-header">&#11088; Favorite countries</div>';
            html+='<div class="music-grid">';
            for(const s of _sortAndFilterStations(pinned))html+=_buildStationCard(s);
            html+='</div><div class="radio-section-header">All stations</div>';
            html+='<div class="music-grid">';
            for(const s of _sortAndFilterStations(rest))html+=_buildStationCard(s);
            html+='</div>';
          }else{
            html+='<div class="music-grid">';
            for(const s of _sortAndFilterStations(stations))html+=_buildStationCard(s);
            html+='</div>';
          }
          if(hasMore){
            html+=`<div style="text-align:center;padding:.6rem 0"><button class="btn btn-dim" onclick="radioLoadMore()" style="min-width:120px">Load more\u2026</button></div>`;
          }
          cards.innerHTML=html;
        }

        function escHtml(s){
          return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
        }

        async function radioPlayStation(encUrl,encName,encCountry,encTag,encStation){
          const url=decodeURIComponent(encUrl);
          const name=decodeURIComponent(encName||'');
          let station=null;
          try{station=encStation?JSON.parse(decodeURIComponent(encStation)):null;}catch{station=null;}
          // Stop current stream first so the server reinitialises cleanly
          await fetch('/api/radio/stop');
          _radioCurrentUrl=url;
          _radioCurrentName=name;
          _radioCurrentCountry=decodeURIComponent(encCountry||'');
          _radioCurrentTag=decodeURIComponent(encTag||'');
          _radioCurrentStation=station;
          _radioIsPlaying=true;
          _radioRetryCount=0;
          document.body.classList.add('radio-player-docked');
          updateRadioBar(name,_radioCurrentCountry,_radioCurrentTag,true,_radioCurrentStation);
          // Update playing highlight without full re-render
          document.querySelectorAll('.radio-station-card').forEach(el=>{
            el.classList.toggle('playing',el.dataset.url===url);
          });
          // Prefetch geo pool for the station's country so dots are populated
          if(_globeDotsEnabled&&station){
            const country=station.countrycode||station.CountryCode
              ||station.country||station.Country||'';
            _refreshGeoPool(country).catch(()=>{});
          }
          // Pre-resolve stream URL via Radio Browser click endpoint (feature 15)
          let playUrl=url;
          try{
            const uuid=station&&(station.stationuuid||station.uuid||station.Uuid)||'';
            if(uuid){
              const r=await fetch('/api/radio/resolve?'+new URLSearchParams({uuid,url}));
              if(r.ok){const j=await r.json();if(j.resolvedUrl)playUrl=j.resolvedUrl;}
            }
          }catch{}
          await fetch('/api/radio/play?'+new URLSearchParams({url:playUrl,name}));
        }

        async function radioToggle(){
          if(_radioIsPlaying){
            await radioStop();   // pauses — bar stays open
          }else if(_radioCurrentUrl){
            // Resume: restart the stream at the same URL
            _radioIsPlaying=true;
            updateRadioBar(_radioCurrentName,_radioCurrentCountry,_radioCurrentTag,true,_radioCurrentStation);
            await fetch('/api/radio/play?'+new URLSearchParams({url:_radioCurrentUrl,name:_radioCurrentName}));
          }
        }

        async function radioStop(){
          await fetch('/api/radio/stop');
          _radioIsPlaying=false;
          // Keep radio-player-docked so the bar stays visible — the user can resume from here.
          updateRadioBar(_radioCurrentName,_radioCurrentCountry,_radioCurrentTag,false,_radioCurrentStation);
        }

        async function radioDismiss(){
          await radioStop();
          _radioCurrentUrl='';
          _radioCurrentName='';
          _radioCurrentCountry='';
          _radioCurrentTag='';
          _radioCurrentStation=null;
          _radioRetryCount=0;
          stopElapsedTick();
          stopWaveform();
          setRadioHealthDot('warn');
          const el=document.getElementById('radio-bar-elapsed');if(el)el.style.display='none';
          const songEl=document.getElementById('radio-bar-song');if(songEl)songEl.style.display='none';
          document.body.classList.remove('radio-player-docked');
          stopGlobeAnim();
          _globeHasStation=false;
        }

        function radioVolume(v){
          const vol=parseFloat(v);
          if(isNaN(vol))return;
          fetch('/api/radio/volume?v='+vol.toFixed(3));
          const lbl=document.getElementById('radio-volume-label');
          if(lbl)lbl.textContent=Math.round(vol*100)+'%';
        }

        function updateRadioBar(title,country,tag,playing,station){
          _radioIsPlaying=playing;
          const t=document.getElementById('radio-bar-title');
          const m=document.getElementById('radio-bar-meta');
          const techText=document.getElementById('radio-bar-tech-text');
          const qBadge=document.getElementById('radio-bar-quality-badge');
          const tagsEl=document.getElementById('radio-bar-tags');
          const popEl=document.getElementById('radio-bar-pop');
          const hpEl=document.getElementById('radio-bar-homepage');
          const favWrap=document.getElementById('radio-bar-favicon-wrap');
          const favImg=document.getElementById('radio-bar-favicon');
          const btn=document.getElementById('radio-btn-play');
          if(t)t.textContent=title||'\u2014';
          if(m){
            const parts=[country,tag].filter(Boolean);
            m.textContent=parts.length?parts.join(' \u00b7 '):'';
          }
          // Extra detail from full station object
          if(station){
            const codec=(station.codec||station.Codec||'').toUpperCase();
            const bitrate=station.bitrate||station.Bitrate||0;
            const hls=station.hls||station.Hls||0;
            const language=station.language||station.Language||'';
            const tags=(station.tags||station.Tags||'').split(',').map(s=>s.trim()).filter(Boolean).slice(0,5).join(', ');
            const votes=station.votes||station.Votes||0;
            const clicks=station.clickcount||station.ClickCount||0;
            const homepage=station.homepage||station.Homepage||'';
            const favicon=station.favicon||station.Favicon||'';
            // Quality badge: codec + bitrate
            if(qBadge){
              const badgeParts=[codec,bitrate?bitrate+'k':''].filter(Boolean);
              if(badgeParts.length){qBadge.textContent=badgeParts.join(' ');qBadge.style.display='';}
              else{qBadge.style.display='none';}
            }
            const techParts=[hls?'HLS':'',language?'\uD83D\uDDE3\uFE0F '+language:''].filter(Boolean);
            if(techText)techText.textContent=techParts.join(' · ');
            if(tagsEl)tagsEl.textContent=tags;
            const popParts=[votes?'\u25B2 '+votes.toLocaleString():'',clicks?'\u25B6 '+clicks.toLocaleString():''].filter(Boolean);
            if(popEl)popEl.textContent=popParts.join('  ');
            if(hpEl){
              if(homepage){
                const label=homepage.replace(/^https?:\/\//,'').split('/')[0];
                hpEl.innerHTML=`<a href="${escHtml(homepage)}" target="_blank" rel="noopener" class="station-homepage">${escHtml(label)}</a>`;
              }else{hpEl.textContent='';}
            }
            if(favImg&&favWrap){
              if(favicon){
                favWrap.style.display='none';
                favImg.onload=()=>{favWrap.style.display='';};
                favImg.onerror=()=>{favWrap.style.display='none';};
                favImg.src=favicon;
              }else{favWrap.style.display='none';}
            }
            renderRadioGlobeAsync(station).catch(()=>{});
          }else{
            if(qBadge)qBadge.style.display='none';
            if(techText)techText.textContent='';
            if(tagsEl)tagsEl.textContent='';
            if(popEl)popEl.textContent='';
            if(hpEl)hpEl.textContent='';
            if(favWrap)favWrap.style.display='none';
            renderRadioGlobeAsync(null).catch(()=>{});
          }
          if(!playing){stopWaveform();stopElapsedTick();const el=document.getElementById('radio-bar-elapsed');if(el)el.style.display='none';}
          if(btn)btn.innerHTML=playing?'\u23F8 Pause':'\u25B6 Play';
        }

        // Country centroid lookup [lat, lon] keyed by ISO 3166-1 alpha-2 code.
        // Used as a fallback when a station has no exact geo_lat/geo_long.
        const _countryCentroids={
          AD:[42.55,1.57],AE:[23.42,53.85],AF:[33.94,67.71],AG:[17.06,-61.80],AL:[41.15,20.17],
          AM:[40.07,45.04],AO:[-11.20,17.87],AR:[-38.42,-63.62],AT:[47.52,14.55],AU:[-25.27,133.78],
          AZ:[40.14,47.58],BA:[43.92,17.68],BB:[13.19,-59.54],BD:[23.68,90.36],BE:[50.50,4.47],
          BF:[12.36,-1.56],BG:[42.73,25.49],BH:[26.02,50.55],BI:[-3.37,29.92],BJ:[9.31,2.32],
          BN:[4.54,114.73],BO:[-16.29,-63.59],BR:[-14.24,-51.93],BS:[25.03,-77.40],BT:[27.51,90.43],
          BW:[-22.33,24.68],BY:[53.71,27.95],BZ:[17.19,-88.50],CA:[56.13,-106.35],CD:[-4.04,21.76],
          CF:[6.61,20.94],CG:[-0.23,15.83],CH:[46.82,8.23],CI:[7.54,-5.55],CL:[-35.68,-71.54],
          CM:[3.85,11.50],CN:[35.86,104.20],CO:[4.57,-74.30],CR:[9.75,-83.75],CU:[21.52,-77.78],
          CV:[16.54,-23.04],CY:[35.13,33.43],CZ:[49.82,15.47],DE:[51.17,10.45],DJ:[11.83,42.59],
          DK:[56.26,9.50],DM:[15.41,-61.37],DO:[18.74,-70.16],DZ:[28.03,1.66],EC:[-1.83,-78.18],
          EE:[58.60,25.01],EG:[26.82,30.80],EH:[24.22,-12.89],ER:[15.18,39.78],ES:[40.46,-3.75],
          ET:[9.15,40.49],FI:[61.92,25.75],FJ:[-16.58,179.41],FK:[-51.80,-59.52],FR:[46.23,2.21],
          GA:[-0.80,11.61],GB:[55.38,-3.44],GD:[12.11,-61.68],GE:[42.32,43.36],GH:[7.95,-1.02],
          GM:[13.44,-15.31],GN:[9.95,-11.81],GQ:[1.65,10.27],GR:[39.07,21.82],GT:[15.78,-90.23],
          GW:[11.80,-15.18],GY:[4.86,-58.93],HN:[15.20,-86.24],HR:[45.10,15.20],HT:[18.97,-72.29],
          HU:[47.16,19.50],ID:[-0.79,113.92],IE:[53.41,-8.24],IL:[31.05,34.85],IN:[20.59,78.96],
          IQ:[33.22,43.68],IR:[32.43,53.69],IS:[64.96,-19.02],IT:[41.87,12.57],JM:[18.11,-77.30],
          JO:[30.59,36.24],JP:[36.20,138.25],KE:[-0.02,37.91],KG:[41.20,74.77],KH:[12.57,104.99],
          KI:[-3.37,-168.73],KM:[-11.64,43.33],KN:[17.36,-62.78],KP:[40.34,127.51],KR:[35.91,127.77],
          KW:[29.31,47.48],KZ:[48.02,66.92],LA:[19.86,102.50],LB:[33.85,35.86],LC:[13.91,-60.98],
          LI:[47.14,9.55],LK:[7.87,80.77],LR:[6.43,-9.43],LS:[-29.61,28.23],LT:[55.17,23.88],
          LU:[49.82,6.13],LV:[56.88,24.60],LY:[26.34,17.23],MA:[31.79,-7.09],MC:[43.75,7.40],
          MD:[47.41,28.37],ME:[42.71,19.37],MG:[-18.77,46.87],MH:[7.13,171.18],MK:[41.61,21.75],
          ML:[17.57,-3.99],MM:[16.87,96.08],MN:[46.86,103.85],MR:[21.01,-10.94],MT:[35.94,14.38],
          MU:[-20.35,57.55],MV:[3.20,73.22],MW:[-13.25,34.30],MX:[23.63,-102.55],MY:[4.21,108.03],
          MZ:[-18.67,35.53],NA:[-22.96,18.49],NE:[17.61,8.08],NG:[9.08,8.68],NI:[12.87,-85.21],
          NL:[52.13,5.29],NO:[60.47,8.47],NP:[28.39,84.12],NR:[-0.52,166.93],NZ:[-40.90,174.89],
          OM:[21.51,55.92],PA:[8.54,-80.78],PE:[-9.19,-75.02],PG:[-6.31,143.96],PH:[12.88,121.77],
          PK:[30.38,69.35],PL:[51.92,19.15],PT:[39.40,-8.22],PW:[7.51,134.58],PY:[-23.44,-58.44],
          QA:[25.35,51.18],RO:[45.94,24.97],RS:[44.02,21.01],RU:[61.52,105.32],RW:[-1.94,29.87],
          SA:[23.89,45.08],SB:[-9.64,160.16],SC:[-4.68,55.49],SD:[12.86,30.22],SE:[60.13,18.64],
          SG:[1.35,103.82],SI:[46.15,14.99],SK:[48.67,19.70],SL:[8.46,-11.78],SM:[43.94,12.46],
          SN:[14.50,-14.45],SO:[5.15,46.20],SR:[3.92,-56.03],SS:[6.88,31.57],ST:[0.19,6.61],
          SV:[13.79,-88.90],SY:[34.80,38.99],SZ:[-26.52,31.47],TD:[15.45,18.73],TG:[8.62,0.82],
          TH:[15.87,100.99],TJ:[38.86,71.28],TL:[-8.87,125.73],TM:[38.97,59.56],TN:[33.89,9.54],
          TO:[-21.18,-175.20],TR:[38.96,35.24],TT:[10.69,-61.22],TV:[-7.11,177.65],TW:[23.70,120.96],
          TZ:[-6.37,34.89],UA:[48.38,31.17],UG:[1.37,32.29],US:[37.09,-95.71],UY:[-32.52,-55.77],
          UZ:[41.38,64.59],VA:[41.90,12.45],VC:[12.98,-61.29],VE:[6.42,-66.59],VN:[14.06,108.28],
          VU:[-15.38,166.96],WS:[-13.76,-172.10],XK:[42.60,20.90],YE:[15.55,48.52],ZA:[-30.56,22.94],
          ZM:[-13.13,27.85],ZW:[-19.02,29.15]
        };

        // ---------------------------------------------------------------------------
        // ─── Radio Globe — ray-cast 3D sphere renderer ────────────────────────────
        // Land polygons + country border arcs decoded once from /world-110m.json.
        // Two equirectangular textures are baked (land fill, border lines) using the
        // browser's own Canvas 2D engine for correct winding/concavity handling.
        // Each frame ray-casts with a zoom factor that animates from 1→4 so the
        // globe starts as a whole-world view then zooms into the station's region.
        // -------------------------------------------------------------------------

        let _topoLandPolys    = null;   // [[lon,lat],...] rings for land fill
        let _topoCountryLines = null;   // [[lon,lat],...] polylines for country borders
        let _usStateLines     = null;   // [[lon,lat],...] polylines for US state borders
        let _topoFetching     = false;
        let _usStateFetching  = false;

        async function ensureTopoLand(){
          if(_topoLandPolys)return _topoLandPolys;
          if(_topoFetching)return null;
          _topoFetching=true;
          try{
            const [topoData]=await Promise.all([
              fetch('/world-110m.json').then(r=>{ if(!r.ok)throw new Error('fetch failed'); return r.json(); }),
              ensureUsStates()
            ]);
            const topo=topoData;
            _topoLandPolys    = decodeTopoLand(topo);
            _topoCountryLines = decodeCountryLines(topo);
            _globeTextureDirty=true;
          }catch(e){
            console.warn('Globe TopoJSON load failed',e);
            _topoLandPolys=[];
            _topoCountryLines=[];
            _topoFetching=false;
          }
          return _topoLandPolys;
        }

        // Decode GeoJSON FeatureCollection of US state polygons into polylines for border rendering.
        async function ensureUsStates(){
          if(_usStateLines)return;
          if(_usStateFetching)return;
          _usStateFetching=true;
          try{
            const r=await fetch('/us-states.json');
            if(!r.ok)throw new Error('us-states fetch failed');
            const geojson=await r.json();
            const lines=[];
            for(const feature of (geojson.features||[])){
              const geom=feature.geometry;
              if(!geom)continue;
              const polys=geom.type==='Polygon'?[geom.coordinates]
                          :geom.type==='MultiPolygon'?geom.coordinates:[];
              for(const poly of polys){
                for(const ring of poly){
                  if(ring.length>=2)lines.push(ring);  // ring: [[lon,lat],...]
                }
              }
            }
            _usStateLines=lines;
            _globeTextureDirty=true;
          }catch(e){
            console.warn('US states load failed',e);
            _usStateLines=[];
          }
        }

        function decodeTopoLand(topo){
          const {scale,translate}=topo.transform;
          const arcs=topo.arcs.map(arc=>{
            let x=0,y=0;
            return arc.map(pt=>{
              x+=pt[0]; y+=pt[1];
              // Keep raw floating-point coords; normalisation happens in splitRingAtAntimeridian
              return[x*scale[0]+translate[0], y*scale[1]+translate[1]];
            });
          });
          function getArc(i){return i>=0?arcs[i]:arcs[~i].slice().reverse();}
          function decodeRing(indices){
            const ring=[];
            for(const i of indices){const pts=getArc(i);for(let j=0;j<pts.length-1;j++)ring.push(pts[j]);}
            return ring;
          }
          const polys=[];
          function processGeom(geom){
            if(!geom)return;
            if(geom.type==='Polygon'){for(const ring of geom.arcs)polys.push(decodeRing(ring));}
            else if(geom.type==='MultiPolygon'){for(const poly of geom.arcs)for(const ring of poly)polys.push(decodeRing(ring));}
            else if(geom.type==='GeometryCollection'){for(const g of geom.geometries)processGeom(g);}
          }
          processGeom(topo.objects.land);
          return polys;
        }

        // Decode every raw arc from the TopoJSON as a polyline — these are the
        // segments that make up country borders (shared edges between countries).
        function decodeCountryLines(topo){
          const {scale,translate}=topo.transform;
          const lines=[];
          for(const arc of topo.arcs){
            let x=0,y=0;
            const line=arc.map(pt=>{x+=pt[0];y+=pt[1];return[x*scale[0]+translate[0],y*scale[1]+translate[1]];});
            if(line.length>=2)lines.push(line);
          }
          return lines;
        }

        // ── Equirectangular textures ──────────────────────────────────────────────
        const TEX_W=1024, TEX_H=512;
        let _globeTex      = null;   // Uint8Array: 1=land
        let _globeBorderTex= null;   // Uint8Array: 1=country border pixel
        let _globeStateTex = null;   // Uint8Array: 1=US state border pixel
        let _globeTextureDirty=true;

        /**
         * Normalise longitude to [-180, 180].
         */
        function normLon(lon){ return ((lon+180)%360+360)%360-180; }

        /**
         * Split a ring of [lon,lat] pairs into sub-rings at the antimeridian.
         *
         * Handles two cases:
         *  a) A decoded TopoJSON ring whose coordinates have drifted outside ±180
         *     (e.g. Russia goes up to lon≈190) — normalise first.
         *  b) A single step that jumps more than 180° (legacy seam crossing).
         *
         * Each output sub-ring is clean: all points inside [-180,180] with no
         * edge that crosses the seam.
         */
        function splitRingAtAntimeridian(ring){
          if(ring.length<2)return[ring];

          // 1. Normalise all longitudes to [-180,180]
          const norm=ring.map(([lon,lat])=>[normLon(lon),lat]);

          // 2. Walk the normalised ring; wherever a segment crosses the antimeridian
          //    (detected by the sign-of-largest-magnitude heuristic), split it.
          const subRings=[];
          let cur=[norm[0]];

          for(let i=1;i<norm.length;i++){
            const [lon0,lat0]=norm[i-1];
            const [lon1,lat1]=norm[i];
            const dLon=lon1-lon0;

            if(Math.abs(dLon)>180){
              // Interpolate the latitude at the antimeridian crossing
              const sign=dLon>0?-1:1;
              const lonA=sign*180;
              const lonB=-sign*180;
              const t=Math.abs((lonA-lon0)/dLon);
              const latX=lat0+(lat1-lat0)*t;
              cur.push([lonA,latX]);
              subRings.push(cur);
              cur=[[lonB,latX],[lon1,lat1]];
            }else{
              cur.push([lon1,lat1]);
            }
          }
          if(cur.length>1)subRings.push(cur);
          return subRings.length?subRings:[norm];
        }

        // Draw rings as filled paths with evenodd winding → land texture
        function buildGlobeTexture(polys){
          const oc=document.createElement('canvas');
          oc.width=TEX_W; oc.height=TEX_H;
          const ctx=oc.getContext('2d');
          ctx.clearRect(0,0,TEX_W,TEX_H);
          ctx.fillStyle='#fff';
          ctx.beginPath();
          for(const ring of polys){
            if(ring.length<2)continue;
            for(const sub of splitRingAtAntimeridian(ring)){
              if(sub.length<2)continue;
              const [lon0,lat0]=sub[0];
              ctx.moveTo((lon0+180)/360*TEX_W,(90-lat0)/180*TEX_H);
              for(let i=1;i<sub.length;i++){
                const [lon,lat]=sub[i];
                ctx.lineTo((lon+180)/360*TEX_W,(90-lat)/180*TEX_H);
              }
              ctx.closePath();
            }
          }
          ctx.fill('evenodd');
          const imgData=ctx.getImageData(0,0,TEX_W,TEX_H).data;
          const tex=new Uint8Array(TEX_W*TEX_H);
          for(let i=0;i<tex.length;i++)tex[i]=imgData[i*4+3]>128?1:0;
          return tex;
        }

        // Draw arc polylines as 1px strokes → border texture
        function buildBorderTexture(lines){
          const oc=document.createElement('canvas');
          oc.width=TEX_W; oc.height=TEX_H;
          const ctx=oc.getContext('2d');
          ctx.clearRect(0,0,TEX_W,TEX_H);
          ctx.strokeStyle='#fff';
          ctx.lineWidth=1;
          for(const line of lines){
            if(line.length<2)continue;
            // Split each polyline at the antimeridian the same way as land rings
            for(const sub of splitRingAtAntimeridian(line)){
              if(sub.length<2)continue;
              ctx.beginPath();
              const [lon0,lat0]=sub[0];
              ctx.moveTo((lon0+180)/360*TEX_W,(90-lat0)/180*TEX_H);
              for(let i=1;i<sub.length;i++){
                const [lon,lat]=sub[i];
                ctx.lineTo((lon+180)/360*TEX_W,(90-lat)/180*TEX_H);
              }
              ctx.stroke();
            }
          }
          const imgData=ctx.getImageData(0,0,TEX_W,TEX_H).data;
          const tex=new Uint8Array(TEX_W*TEX_H);
          for(let i=0;i<tex.length;i++)tex[i]=imgData[i*4+3]>64?1:0;
          return tex;
        }

        function globeTexLookup(latRad,lonRad){
          if(!_globeTex)return 0;
          let lo=lonRad*180/Math.PI;
          lo=((lo+180)%360+360)%360-180;
          const u=(lo+180)/360*(TEX_W-1);
          const la=latRad*180/Math.PI;
          const v=(90-la)/180*(TEX_H-1);
          const px=Math.min(TEX_W-1,Math.max(0,Math.round(u)));
          const py=Math.min(TEX_H-1,Math.max(0,Math.round(v)));
          return _globeTex[py*TEX_W+px];
        }

        function borderTexLookup(latRad,lonRad){
          if(!_globeBorderTex)return 0;
          let lo=lonRad*180/Math.PI;
          lo=((lo+180)%360+360)%360-180;
          const u=(lo+180)/360*(TEX_W-1);
          const la=latRad*180/Math.PI;
          const v=(90-la)/180*(TEX_H-1);
          const px=Math.min(TEX_W-1,Math.max(0,Math.round(u)));
          const py=Math.min(TEX_H-1,Math.max(0,Math.round(v)));
          return _globeBorderTex[py*TEX_W+px];
        }

        function stateTexLookup(latRad,lonRad){
          if(!_globeStateTex)return 0;
          let lo=lonRad*180/Math.PI;
          lo=((lo+180)%360+360)%360-180;
          const u=(lo+180)/360*(TEX_W-1);
          const la=latRad*180/Math.PI;
          const v=(90-la)/180*(TEX_H-1);
          const px=Math.min(TEX_W-1,Math.max(0,Math.round(u)));
          const py=Math.min(TEX_H-1,Math.max(0,Math.round(v)));
          return _globeStateTex[py*TEX_W+px];
        }

        // ── Globe animation state ─────────────────────────────────────────────────
        let _globeAnimId     =null;
        let _globeTargetLat  =0, _globeTargetLon=0;
        let _globeCurrentLat =0, _globeCurrentLon=0;
        let _globeIdleLon    =0;
        let _globeHasStation =false;
        let _globeLastTime   =0;
        let _globeZoom       =1.0;   // current zoom (1=whole globe, 4=zoomed in)
        let _globeZoomTarget =1.0;   // animated toward this
        let _globePingPhase  =0;     // 0..1 drives the station-dot ping ring
        // Transition state machine: 'steady' | 'zoomout'
        // When a new station arrives while zoomed in, zoom out first then rotate.
        let _globeTransitState  ='steady';
        let _globePendingLat    =0;
        let _globePendingLon    =0;
        let _globePendingRegion ='';  // 'europe' | 'usa' | ''
        let _globeCurrentRegion ='';  // active region for renderer
        const GLOBE_SNAP_SPEED       = 2.5;
        const GLOBE_ZOOM_TARGET      = 4.0;
        const GLOBE_ZOOM_SPEED       = 0.6;  // exponential approach rate

        function startGlobeAnim(canvas){
          if(_globeAnimId)return;
          _globeLastTime=performance.now();
          function frame(now){
            const dt=Math.min((now-_globeLastTime)/1000,0.1);
            _globeLastTime=now;
            if(_globeHasStation){
                if(_globeTransitState==='zoomout'){
                  // Phase 1: zoom back out to globe view before rotating
                  _globeZoomTarget=1.0;
                  const kz=1-Math.exp(-GLOBE_ZOOM_SPEED*dt);
                  _globeZoom+=(_globeZoomTarget-_globeZoom)*kz;
                  // Phase 2: once nearly at zoom=1, apply pending station and start zoom-in
                  if(_globeZoom<1.12){
                    _globeZoom=1.0;
                    _globeTargetLat=_globePendingLat;
                    _globeTargetLon=_globePendingLon;
                    _globeZoomTarget=_globePendingRegion?GLOBE_ZOOM_TARGET:1.0;
                    _globeCurrentRegion=_globePendingRegion;
                    _globePingPhase=0;
                    _globeTransitState='steady';
                  }
                  // Keep globe centred on old location during zoom-out
                } else {
                  // Normal: rotate toward target then zoom
                  const k=1-Math.exp(-GLOBE_SNAP_SPEED*dt);
                  _globeCurrentLat+=(_globeTargetLat-_globeCurrentLat)*k;
                  let dLon=_globeTargetLon-_globeCurrentLon;
                  if(dLon>Math.PI)dLon-=2*Math.PI;
                  else if(dLon<-Math.PI)dLon+=2*Math.PI;
                  _globeCurrentLon+=dLon*k;
                  const kz=1-Math.exp(-GLOBE_ZOOM_SPEED*dt);
                  _globeZoom+=(_globeZoomTarget-_globeZoom)*kz;
                }
              } else {
                _globeIdleLon+=GLOBE_IDLE_DEG_PER_SEC*Math.PI/180*dt;
                _globeZoom=1.0;
                _globeZoomTarget=1.0;
                _globeTransitState='steady';
              }
            _globePingPhase=(_globePingPhase+dt*0.7)%1;
            drawGlobeFrame(canvas);
            _globeAnimId=requestAnimationFrame(frame);
          }
          _globeAnimId=requestAnimationFrame(frame);
        }

        function stopGlobeAnim(){
          if(_globeAnimId){cancelAnimationFrame(_globeAnimId);_globeAnimId=null;}
        }

        // ── Frame renderer ────────────────────────────────────────────────────────
        function drawGlobeFrame(canvas){
          const W=canvas.width, H=canvas.height;
          const ctx=canvas.getContext('2d');
          if(!ctx)return;

          if(_globeTextureDirty&&_topoLandPolys){
            _globeTex      =buildGlobeTexture(_topoLandPolys);
            _globeBorderTex=_topoCountryLines?buildBorderTexture(_topoCountryLines):null;
            _globeStateTex =_usStateLines?buildBorderTexture(_usStateLines):null;
            _globeTextureDirty=false;
          }

          const img=ctx.createImageData(W,H);
          const d=img.data;

          const cx=W/2, cy=H/2;
          const R=W/2-1;

          // Light: upper-left, slightly in front
          const lx=-0.55, ly=0.55, lz=0.63;
          const lLen=Math.sqrt(lx*lx+ly*ly+lz*lz);
          const Lx=lx/lLen, Ly=ly/lLen, Lz=lz/lLen;
          const hLen=Math.sqrt(Lx*Lx+Ly*Ly+(Lz+1)*(Lz+1));
          const Hx=Lx/hLen, Hy=Ly/hLen, Hz=(Lz+1)/hLen;

          const viewLat=_globeHasStation ? _globeCurrentLat : 0;
          const viewLon=_globeHasStation ? _globeCurrentLon : _globeIdleLon;
          const cosLat=Math.cos(viewLat), sinLat=Math.sin(viewLat);

          // Zoom: shrink the projected (nx,ny) so fewer degrees are visible.
          // At zoom=1 the full hemisphere is visible; at zoom=4 only 1/4 is shown.
          const zoom=Math.max(1, _globeZoom);

          // Grid spacing adapts to zoom: coarse at zoom≈1, fine at zoom≥3
          const gridDeg = zoom < 1.8 ? 30 : zoom < 3 ? 15 : 10;

          // Border opacity: 0 below zoom 1.5, ramps to 1 at zoom 2.5
          const borderAlpha=Math.max(0, Math.min(1, (_globeZoom-1.5)/1.0));

          // Smoothly morph border-radius: circle (50%) at zoom=1, square (0%) when fully zoomed
          const brPct=Math.max(0,Math.round(50*(1-(_globeZoom-1)/(GLOBE_ZOOM_TARGET-1))));
          canvas.style.borderRadius=brPct+'%';

          // Background colour for corners outside the sphere disk
          const BG_R=3,BG_G=8,BG_B=5;

          for(let py=0;py<H;py++){
            const dy=(py-cy)/R;
            for(let px=0;px<W;px++){
              const dx=(px-cx)/R;
              const off=(py*W+px)*4;

              // Project through zoom: a corner pixel maps to a much closer-to-centre
              // sphere point when zoomed in, allowing the full canvas square to be filled.
              const sx=dx/zoom, sy=dy/zoom;
              const sz2=sx*sx+sy*sy;
              if(sz2>1){
                // Outside the zoomed geographic window — dark background
                d[off+0]=BG_R; d[off+1]=BG_G; d[off+2]=BG_B; d[off+3]=255;
                continue;
              }

              // Use zoomed sphere coordinates for both normals and geographic projection.
              // This is correct: the sphere normal at the hit point is (sx, -sy, sqrt(1-sz2)).
              const nx= sx;
              const ny=-sy;
              const nz= Math.sqrt(1-sz2);

              // Alias for clarity (same values)
              const hnx=nx, hny=ny, hnz=nz;

              // Inverse orthographic projection → geographic lat/lon
              const geoLat=Math.asin(Math.max(-1,Math.min(1, hnz*sinLat + hny*cosLat)));
              const geoLon=viewLon + Math.atan2(hnx, cosLat*hnz - sinLat*hny);

              const isLand  =globeTexLookup(geoLat,geoLon);
              const isBorder=borderAlpha>0 && borderTexLookup(geoLat,geoLon);
              const isState =borderAlpha>0 && _globeCurrentRegion==='usa' && stateTexLookup(geoLat,geoLon);

              // Base colours
              let br=isLand?40:7, bg=isLand?88:25, bb=isLand?38:14;

              // Grid lines
              const gLat=geoLat*180/Math.PI, gLon=geoLon*180/Math.PI;
              const modLat=((gLat%gridDeg)+gridDeg)%gridDeg;
              const modLon=((gLon%gridDeg)+gridDeg)%gridDeg;
              const gridThr=Math.max(0.5, 0.8/zoom);
              const onGrid=(modLat<gridThr||modLat>gridDeg-gridThr)||(modLon<gridThr||modLon>gridDeg-gridThr);
              if(onGrid){br+=8;bg+=11;bb+=8;}

              // Country borders (faded in as zoom increases)
              if(isBorder){
                const bStr=borderAlpha*0.85;
                br=Math.round(br*(1-bStr)+(isLand?90:40)*bStr);
                bg=Math.round(bg*(1-bStr)+(isLand?160:80)*bStr);
                bb=Math.round(bb*(1-bStr)+(isLand?75:50)*bStr);
              }

              // US state borders — slightly subtler than country borders
              if(isState){
                const sStr=borderAlpha*0.65;
                br=Math.round(br*(1-sStr)+(isLand?75:35)*sStr);
                bg=Math.round(bg*(1-sStr)+(isLand?135:65)*sStr);
                bb=Math.round(bb*(1-sStr)+(isLand?60:40)*sStr);
              }

              // Phong lighting
              const diff =Math.max(0, nx*Lx+ny*Ly+nz*Lz);
              const nDotH=Math.max(0, nx*Hx+ny*Hy+nz*Hz);
              const spec =Math.pow(nDotH,80)*(isLand?0.04:0.18);
              const light=0.20+0.75*diff;

              d[off+0]=Math.min(255, br*light+spec*230);
              d[off+1]=Math.min(255, bg*light+spec*255);
              d[off+2]=Math.min(255, bb*light+spec*200);
              d[off+3]=255;
            }
          }

          ctx.putImageData(img,0,0);

          // Atmosphere rim — only visible when near globe view (fades out with zoom)
          const rimOpacity = Math.max(0, 1 - (_globeZoom - 1) / 0.8);
          if (rimOpacity > 0.01) {
            const rim=ctx.createRadialGradient(cx,cy,R*0.82,cx,cy,R);
            rim.addColorStop(0,'rgba(0,0,0,0)');
            rim.addColorStop(1,`rgba(0,30,10,${(0.55*rimOpacity).toFixed(3)})`);
            ctx.fillStyle=rim;
            ctx.beginPath();ctx.arc(cx,cy,R,0,2*Math.PI);ctx.fill();
          }

          // Station dot + animated ping ring at canvas centre
          if(_globeHasStation){
            // Ping ring: expands from radius 5 to 18, fades out
            const pingR=5+_globePingPhase*13;
            const pingOpacity=Math.max(0,1-_globePingPhase)*0.75;
            ctx.save();
            ctx.globalAlpha=pingOpacity;
            ctx.strokeStyle='#e94560';
            ctx.lineWidth=1.5;
            ctx.beginPath();ctx.arc(cx,cy,pingR,0,2*Math.PI);ctx.stroke();
            ctx.restore();

            // Solid dot
            ctx.save();
            ctx.shadowColor='#e94560';
            ctx.shadowBlur=8;
            ctx.fillStyle='#e94560';
            ctx.beginPath();ctx.arc(cx,cy,4,0,2*Math.PI);ctx.fill();
            ctx.restore();
          }

          // Additional station dots for all visible stations (feature 10)
          _drawGlobeStationDots(canvas);
        }

        // ── Public entry point (called from updateRadioBar) ───────────────────────
        async function renderRadioGlobeAsync(station){
          await ensureTopoLand();
          renderRadioGlobe(station);
        }

        // ── Globe station dots (feature 10/11/12) ─────────────────────────────────
        let _globeDotsEnabled=false;
        let _globeGeoStations=[];    // full country geo-pool for dot rendering
        let _globeGeoCountry='';    // which country the pool was fetched for
        let _globeGeoFetching=false;

        /** Fetch up to 500 geo-located stations for `country`; fills _globeGeoStations. */
        async function _refreshGeoPool(country){
          if(!country||_globeGeoFetching)return;
          if(country===_globeGeoCountry&&_globeGeoStations.length)return; // already loaded
          _globeGeoFetching=true;
          try{
            const params=new URLSearchParams({country,limit:'500',offset:'0',q:'',tag:''});
            const r=await fetch('/api/radio/search?'+params);
            if(r.ok){
              const arr=await r.json();
              _globeGeoStations=arr;
              _globeGeoCountry=country;
            }
          }catch{}
          _globeGeoFetching=false;
        }

        function radioToggleGlobeDots(){
          _globeDotsEnabled=!_globeDotsEnabled;
          const btn=document.getElementById('radio-globe-dots-btn');
          if(btn)btn.classList.toggle('active',_globeDotsEnabled);
          // Prefetch geo pool for current station's country when enabling
          if(_globeDotsEnabled&&_radioCurrentStation){
            const country=_radioCurrentStation.countrycode||_radioCurrentStation.CountryCode
              ||_radioCurrentStation.country||_radioCurrentStation.Country||'';
            _refreshGeoPool(country).catch(()=>{});
          }
          // Re-render immediately with current station
          if(_radioCurrentStation)renderRadioGlobeAsync(_radioCurrentStation).catch(()=>{});
        }

        /**
         * Project geographic [lonDeg,latDeg] onto the globe canvas pixel coordinates.
         * This is the exact forward inverse of the pixel-shader's orthographic projection:
         *   shader inverse: geoLat = asin(nz*sinViewLat + ny*cosViewLat)
         *                   geoLon = viewLon + atan2(nx, cosViewLat*nz - sinViewLat*ny)
         * So forward (geo→view) is: rotation around X by +viewLat, then orthographic project.
         */
        function _globeProject(lonDeg,latDeg,canvas){
          const W=canvas.width, H=canvas.height;
          const R=W/2-1;                     // must match drawGlobeFrame exactly
          const cx=W/2, cy=H/2;
          const relLon=lonDeg*Math.PI/180-_globeCurrentLon;
          const geoLat=latDeg*Math.PI/180;
          const viewLat=_globeCurrentLat;    // already in radians
          // Unit-sphere point in geographic frame
          const x=Math.cos(geoLat)*Math.sin(relLon);
          const y=Math.sin(geoLat);
          const z=Math.cos(geoLat)*Math.cos(relLon);
          // Rotate around X axis by +viewLat (forward transform)
          const cosVL=Math.cos(viewLat);
          const sinVL=Math.sin(viewLat);
          const nx=x;
          const ny=y*cosVL-z*sinVL;
          const nz=y*sinVL+z*cosVL;
          if(nz<=0)return null; // behind the globe
          const zoom=Math.max(1,_globeZoom);
          return{
            px:cx+nx*zoom*R,
            py:cy-ny*zoom*R
          };
        }

        /** Normalise a raw lat/lon value (number or string) to float or null. */
        function _parseCoord(v){
          if(v===null||v===undefined||v==='')return null;
          const n=typeof v==='number'?v:parseFloat(v);
          return isNaN(n)?null:n;
        }

        /** Draw station dots for all currently loaded stations on the globe canvas. */
        function _drawGlobeStationDots(canvas){
          if(!_globeDotsEnabled)return;
          const ctx=canvas.getContext('2d');
          // Merge browser page + geo pool + favorites; deduplicate by uuid
          const stations=[..._globeGeoStations,..._radioStations,..._radioFavorites];
          const seen=new Set();
          const accent=getComputedStyle(document.body).getPropertyValue('--player-accent').trim()||'#e94560';
          for(const s of stations){
            const uuid=s.stationuuid||s.uuid||s.Uuid||'';
            if(uuid&&seen.has(uuid))continue;
            if(uuid)seen.add(uuid);
            const lat=_parseCoord(s.geo_lat??s.GeoLat);
            const lon=_parseCoord(s.geo_long??s.GeoLong);
            if(lat===null||lon===null||(lat===0&&lon===0))continue;
            const pt=_globeProject(lon,lat,canvas);
            if(!pt)continue;
            const isPlaying=_radioCurrentUrl&&(s.url_resolved||s.streamUrl||s.StreamUrl||s.url||s.Url)===_radioCurrentUrl;
            ctx.save();
            ctx.beginPath();
            ctx.arc(pt.px,pt.py,isPlaying?5:3,0,2*Math.PI);
            ctx.fillStyle=isPlaying?accent:'rgba(255,255,255,0.75)';
            ctx.shadowColor=isPlaying?accent:'rgba(0,0,0,0.6)';
            ctx.shadowBlur=isPlaying?8:3;
            ctx.fill();
            ctx.restore();
          }
        }

        /** Hit-test globe canvas for a mouse position; return station or null. */
        function _globeHitTest(canvas,offsetX,offsetY){
          if(!_globeDotsEnabled)return null;
          const stations=[..._globeGeoStations,..._radioStations,..._radioFavorites];
          const seen=new Set();
          for(const s of stations){
            const uuid=s.stationuuid||s.uuid||s.Uuid||'';
            if(uuid&&seen.has(uuid))continue;
            if(uuid)seen.add(uuid);
            const lat=_parseCoord(s.geo_lat??s.GeoLat);
            const lon=_parseCoord(s.geo_long??s.GeoLong);
            if(lat===null||lon===null||(lat===0&&lon===0))continue;
            const pt=_globeProject(lon,lat,canvas);
            if(!pt)continue;
            const dx=offsetX-pt.px,dy=offsetY-pt.py;
            if(dx*dx+dy*dy<=100)return{station:s,px:pt.px,py:pt.py}; // 10px radius
          }
          return null;
        }

        function radioGlobeMouseMove(event){
          const canvas=document.getElementById('radio-globe-canvas');
          const tooltip=document.getElementById('radio-globe-tooltip');
          if(!canvas||!tooltip)return;
          const hit=_globeHitTest(canvas,event.offsetX,event.offsetY);
          if(!hit){tooltip.style.display='none';return;}
          const s=hit.station;
          const name=s.name||s.Name||'?';
          const bitrate=s.bitrate||s.Bitrate||0;
          const codec=(s.codec||s.Codec||'').toUpperCase();
          let tip=escHtml(name);
          if(codec||bitrate)tip+=`<br><small>${escHtml([codec,bitrate?bitrate+'k':''].filter(Boolean).join(' '))}</small>`;
          tooltip.innerHTML=tip;
          tooltip.style.display='block';
          tooltip.style.left=(hit.px+6)+'px';
          tooltip.style.top=(hit.py-24)+'px';
        }

        function radioGlobeMouseLeave(){
          const tooltip=document.getElementById('radio-globe-tooltip');
          if(tooltip)tooltip.style.display='none';
        }

        function radioGlobeClick(event){
          const canvas=document.getElementById('radio-globe-canvas');
          if(!canvas)return;
          const hit=_globeHitTest(canvas,event.offsetX,event.offsetY);
          if(!hit)return;
          const s=hit.station;
          const url=s.url_resolved||s.streamUrl||s.StreamUrl||'';
          const name=s.name||s.Name||'';
          if(!url)return;
          const country=s.country||s.Country||'';
          const tag=(s.tags||s.Tags||'').split(',').filter(Boolean)[0]||'';
          radioPlayStation(
            encodeURIComponent(url),
            encodeURIComponent(name),
            encodeURIComponent(country),
            encodeURIComponent(tag),
            encodeURIComponent(JSON.stringify(s))
          );
          radioGlobeMouseLeave();
        }

        function renderRadioGlobe(station){
          const geoWrap=document.getElementById('radio-bar-geo');
          const canvas=document.getElementById('radio-globe-canvas');
          const label=document.getElementById('radio-geo-label');
          if(!geoWrap||!canvas||!label)return;

          let lat=station&&(station.geo_lat??station.GeoLat??null);
          let lon=station&&(station.geo_long??station.GeoLong??null);
          let hasCoords=typeof lat==='number'&&typeof lon==='number'&&!(lat===0&&lon===0);
          const countryName=station&&(station.country||station.Country||'');
          const stateName=station&&(station.state||station.State||'');
          const countryCode=station&&(station.countrycode||station.CountryCode||'');

          if(!hasCoords&&countryCode){
            const c=_countryCentroids[countryCode.toUpperCase()];
            if(c){lat=c[0];lon=c[1];hasCoords=true;}
          }

          if(!station||(!hasCoords&&!countryName)){
            geoWrap.style.display='none';
            stopGlobeAnim();
            return;
          }
          geoWrap.style.display='flex';

          const labelParts=[];
          if(countryName)labelParts.push('\uD83C\uDF10 '+countryName);
          if(stateName)labelParts.push('\uD83D\uDCCD '+stateName);
          const rawLat=station&&(station.geo_lat??station.GeoLat??null);
          const rawLon=station&&(station.geo_long??station.GeoLong??null);
          const hasRawCoords=typeof rawLat==='number'&&typeof rawLon==='number'&&!(rawLat===0&&rawLon===0);
          if(hasRawCoords)labelParts.push(rawLat.toFixed(2)+'\u00b0, '+rawLon.toFixed(2)+'\u00b0');
          label.textContent=labelParts.join('\n');

          if(!hasCoords){
            canvas.style.display='none';
            stopGlobeAnim();
            return;
          }
          canvas.style.display='block';

          // Detect new station BEFORE updating targets
          const newLatRad = lat * Math.PI / 180;
          const newLonRad = lon * Math.PI / 180;
          const isNewStation = !_globeHasStation
            || _globeTargetLat !== newLatRad
            || _globeTargetLon !== newLonRad;

          // Europe: lat 35..72°N, lon -25..45°E
          const isEurope = (lat >= 35 && lat <= 72 && lon >= -25 && lon <= 45);
          // Contiguous USA + Alaska: lat 18..72, lon -170..-65
          const isUSA    = (lat >= 18 && lat <= 72 && lon >= -170 && lon <= -65);
          const region   = isEurope ? 'europe' : isUSA ? 'usa' : '';

          const wasFirstStation = !_globeHasStation;

          if (isNewStation) {
            _globeHasStation = true;
            const sameRegion = region && region === _globeCurrentRegion;
            if (_globeZoom > 1.3 && !sameRegion) {
              // Currently zoomed into a different region — zoom out first
              _globePendingLat    = newLatRad;
              _globePendingLon    = newLonRad;
              _globePendingRegion = region;
              _globeZoomTarget    = 1.0;
              _globeTransitState  = 'zoomout';
            } else if (_globeZoom > 1.3 && sameRegion) {
              // Same region, just rotate — keep zoom
              _globeTargetLat    = newLatRad;
              _globeTargetLon    = newLonRad;
              _globeZoomTarget   = GLOBE_ZOOM_TARGET;
              _globePingPhase    = 0;
              _globeTransitState = 'steady';
            } else {
              // Not zoomed: animate rotation then zoom
              _globeTargetLat  = newLatRad;
              _globeTargetLon  = newLonRad;
              if (wasFirstStation) {
                _globeCurrentLat = newLatRad;
                _globeCurrentLon = newLonRad;
              }
              _globeZoom          = 1.0;
              _globeZoomTarget    = region ? GLOBE_ZOOM_TARGET : 1.0;
              _globeCurrentRegion = region;
              _globePingPhase     = 0;
              _globeTransitState  = 'steady';
            }
          } else if (!_globeAnimId) {
            // Re-entering same station (e.g. panel reopened) — just resume
            _globeTargetLat  = newLatRad;
            _globeTargetLon  = newLonRad;
            _globeHasStation = true;
          }
          startGlobeAnim(canvas);
        }


        let _radioStatusPollId=null;
        let _radioElapsedBase=0; // server-reported elapsed at last poll
        let _radioElapsedPollTime=0; // performance.now() when that poll landed
        let _radioElapsedTick=null;
        let _radioRetryCount=0;
        let _radioWaveformId=null;
        let _radioWaveformPhase=0;

        function startRadioStatusPoll(){
          stopRadioStatusPoll();
          _radioStatusPollId=setInterval(radioStatusTick,2500);
        }
        function stopRadioStatusPoll(){
          if(_radioStatusPollId){clearInterval(_radioStatusPollId);_radioStatusPollId=null;}
          stopElapsedTick();
          stopWaveform();
        }

        // ── Elapsed clock ────────────────────────────────────────────────────
        function startElapsedTick(){
          stopElapsedTick();
          _radioElapsedTick=setInterval(()=>{
            if(!_radioIsPlaying)return;
            const secNow=_radioElapsedBase+Math.round((performance.now()-_radioElapsedPollTime)/1000);
            const el=document.getElementById('radio-bar-elapsed');
            if(el){el.textContent=fmtSec(secNow);el.style.display='';}
          },1000);
        }
        function stopElapsedTick(){
          if(_radioElapsedTick){clearInterval(_radioElapsedTick);_radioElapsedTick=null;}
        }
        function fmtSec(s){
          const h=Math.floor(s/3600);
          const m=Math.floor((s%3600)/60);
          const sec=s%60;
          if(h>0)return `${h}:${String(m).padStart(2,'0')}:${String(sec).padStart(2,'0')}`;
          return `${m}:${String(sec).padStart(2,'0')}`;
        }

        // ── Waveform animator ────────────────────────────────────────────────
        function startWaveform(){
          const cv=document.getElementById('radio-waveform');
          if(!cv)return;
          cv.classList.add('active');
          stopWaveform();
          function draw(){
            _radioWaveformPhase+=0.07;
            const ctx=cv.getContext('2d');
            const W=cv.width,H=cv.height;
            ctx.clearRect(0,0,W,H);
            const bars=10;
            const bw=4,gap=2;
            const totalW=bars*(bw+gap)-gap;
            const x0=Math.round((W-totalW)/2);
            const accent=getComputedStyle(document.body).getPropertyValue('--player-accent').trim()||'#e94560';
            ctx.fillStyle=accent;
            for(let i=0;i<bars;i++){
              const t=_radioWaveformPhase+i*0.6;
              const h=Math.round(4+Math.abs(Math.sin(t))*(H-8));
              const y=Math.round((H-h)/2);
              ctx.fillRect(x0+i*(bw+gap),y,bw,h);
            }
            _radioWaveformId=requestAnimationFrame(draw);
          }
          draw();
        }
        function stopWaveform(){
          if(_radioWaveformId){cancelAnimationFrame(_radioWaveformId);_radioWaveformId=null;}
          const cv=document.getElementById('radio-waveform');
          if(cv){cv.classList.remove('active');const ctx=cv.getContext('2d');ctx.clearRect(0,0,cv.width,cv.height);}
        }

        // ── Health dot ───────────────────────────────────────────────────────
        function setRadioHealthDot(state){ // 'ok'|'warn'|'error'|'off'
          const dot=document.getElementById('radio-health-dot');
          if(!dot)return;
          dot.className='radio-health-dot';
          if(state==='ok')dot.classList.add('ok');
          else if(state==='error')dot.classList.add('error');
          // 'warn' = default amber; 'off' = hidden via amber (same visual as warn)
        }

        async function radioStatusTick(){
          try{
            const r=await fetch('/api/radio/playback-status');
            if(!r.ok){setRadioHealthDot('error');return;}
            const s=await r.json();
            const playing=s.isPlaying||s.IsPlaying||false;
            const stalled=s.isStalled||s.IsStalled||false;
            const name=s.stationName||s.StationName||_radioCurrentName||'';
            const url=s.stationUrl||s.StationUrl||'';
            const songTitle=s.streamTitle||s.StreamTitle||'';
            const elapsed=s.elapsedSeconds||s.ElapsedSeconds||0;
            const err=s.error||s.Error||'';
            if(url)_radioCurrentUrl=url;

            // Update elapsed clock base
            if(playing&&elapsed>0){
              _radioElapsedBase=elapsed;
              _radioElapsedPollTime=performance.now();
              if(!_radioElapsedTick)startElapsedTick();
            }

            // Health dot
            if(!playing&&err){setRadioHealthDot('error');}
            else if(stalled){setRadioHealthDot('warn');}
            else if(playing){setRadioHealthDot('ok');}
            else{setRadioHealthDot('warn');}

            // Song title marquee
            const songEl=document.getElementById('radio-bar-song');
            const songTextEl=document.getElementById('radio-bar-song-text');
            if(songEl&&songTextEl){
              if(songTitle&&playing){
                songTextEl.textContent=songTitle;
                songEl.style.display='';
              }else{
                songEl.style.display='none';
              }
            }

            // Waveform
            if(playing&&!stalled){startWaveform();}
            else{stopWaveform();}

            // Auto-retry on stall (feature 13)
            if(stalled&&_radioCurrentUrl&&_radioIsPlaying){
              _radioRetryCount++;
              const delay=Math.min(3000*_radioRetryCount,15000);
              setTimeout(async()=>{
                if(_radioIsPlaying&&_radioCurrentUrl){
                  await fetch('/api/radio/play?'+new URLSearchParams({url:_radioCurrentUrl,name:_radioCurrentName}));
                  _radioRetryCount=0;
                }
              },delay);
            }else if(!stalled){
              _radioRetryCount=0;
            }

            // Notify server audio is alive (so stall detection keeps ticking)
            if(playing)fetch('/api/radio/notify-alive').catch(()=>{});

            updateRadioBar(name,_radioCurrentCountry,_radioCurrentTag,playing,_radioCurrentStation);
            // Sync volume slider
            const vol=s.volume??s.Volume??0.8;
            const vSlider=document.getElementById('radio-bar-volume');
            const vLabel=document.getElementById('radio-volume-label');
            if(vSlider&&Math.abs(parseFloat(vSlider.value)-vol)>0.02)vSlider.value=vol;
            if(vLabel)vLabel.textContent=Math.round(vol*100)+'%';
          }catch{}
        }

        async function radioToggleFav(btn,encStation){
          try{
            const station=JSON.parse(decodeURIComponent(encStation));
            const r=await fetch('/api/radio/favorite',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(station)});
            if(!r.ok)return;
            const resp=await r.json();
            const isFav=resp.isFavorite||false;
            await radioLoadFavorites();
            // Re-render so removed favorites disappear from favorites tab, and hearts update elsewhere
            if(_radioTab==='favorites'){
              renderRadioCards(_radioFavorites,false);
            }else{
              renderRadioCards(_radioStations,_radioStations.length===_radioPageSize*(_radioPage+1));
            }
          }catch{}
        }
