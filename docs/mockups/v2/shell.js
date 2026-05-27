// 主题切换 + 共享侧栏注入
(function () {
  const saved = localStorage.getItem('dc-theme');
  if (saved === 'dark') document.body.classList.add('dark');

  const NAV = [
    { k: 'dashboard', t: '仪表盘', ic: '⌂' },
    { grp: '采集' },
    { k: 'workspace', t: '采集任务', ic: '▤' },
    { k: 'browse', t: '浏览节点', ic: '⌕' },
    { grp: '全局监控' },
    { k: 'livedata', t: '实时数据', ic: '∿' },
    { k: 'diagnostics', t: '诊断', ic: '♥' },
    { grp: '系统' },
    { k: 'settings', t: '设置', ic: '⚙' },
    { k: 'logs', t: '日志', ic: '≡' },
  ];

  window.renderShell = function (activeKey) {
    const nav = document.querySelector('.nav');
    if (nav) {
      let html = '';
      for (const n of NAV) {
        if (n.grp) { html += `<div class="grp">${n.grp}</div>`; continue; }
        const on = n.k === activeKey ? ' on' : '';
        html += `<div class="item${on}" onclick="location.href='${n.k}.html'"><span class="ic">${n.ic}</span>${n.t}</div>`;
      }
      html += `<div class="foot"></div><div class="item" onclick="location.href='dialog-task.html'"><span class="ic">ⓘ</span>关于</div>`;
      nav.innerHTML = html;
    }
    const tb = document.querySelector('.theme');
    if (tb) tb.onclick = () => {
      document.body.classList.toggle('dark');
      localStorage.setItem('dc-theme', document.body.classList.contains('dark') ? 'dark' : 'light');
      tb.textContent = document.body.classList.contains('dark') ? '☀ 亮色' : '🌙 暗色';
    };
    if (tb) tb.textContent = document.body.classList.contains('dark') ? '☀ 亮色' : '🌙 暗色';
  };
})();
