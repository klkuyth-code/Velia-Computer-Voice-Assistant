// @vitest-environment jsdom

import { beforeEach, describe, expect, it } from 'vitest';
import { captureArticle, capturePage, isHighRiskElement, rebuildRegistry, setEditableValue } from '../src/dom';

describe('bounded DOM snapshot', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
    Object.defineProperty(HTMLElement.prototype, 'getBoundingClientRect', {
      configurable: true,
      value: () => ({ x: 0, y: 0, top: 0, left: 0, right: 100, bottom: 30, width: 100, height: 30, toJSON: () => ({}) }),
    });
  });

  it('prioritizes media links and never returns password values', () => {
    document.body.innerHTML = `
      <button>普通按钮</button>
      <a href="https://example.com/video/42">目标视频</a>
      <input type="password" value="secret-password">
    `;
    const { snapshot } = capturePage(document);

    expect(snapshot.elements[0]?.href).toContain('/video/42');
    expect(JSON.stringify(snapshot)).not.toContain('secret-password');
  });

  it('blocks high-risk controls and updates safe editable fields', () => {
    document.body.innerHTML = '<button id="pay">确认支付</button><input id="search">';
    const pay = document.getElementById('pay')!;
    const input = document.getElementById('search')!;

    expect(isHighRiskElement(pay)).toBe(true);
    setEditableValue(input, 'Velia test');
    expect((input as HTMLInputElement).value).toBe('Velia test');
  });

  it('preserves element references when the content registry is rebuilt', () => {
    document.body.innerHTML = '<a href="https://iana.org/domains/example">Learn more</a>';
    const first = capturePage(document);
    const ref = first.snapshot.elements[0]?.ref;

    const rebuilt = rebuildRegistry(document);
    const second = capturePage(document);

    expect(ref).toMatch(/^v[a-f0-9]{12}$/u);
    expect(rebuilt.elements.get(ref!)).toBe(document.querySelector('a'));
    expect(second.snapshot.elements[0]?.ref).toBe(ref);
  });

  it('marks heading links as ordered search-result candidates', () => {
    document.body.innerHTML = `
      <nav><a href="https://example.com/images">图片</a></nav>
      <section><span>赞助</span><a href="https://ads.example/?gclid=paid"><h3>推广结果</h3></a></section>
      <a href="https://paid.example/?gclid=tracked"><h3>没有文字标记的推广结果</h3></a>
      <a href="https://first.example/"><h3>第一条结果</h3></a>
      <h3><a href="https://second.example/">第二条结果</a></h3>
      <a id="third" href="https://third.example/"><h3>第三条屏幕外结果</h3></a>
    `;
    Object.defineProperty(document.getElementById('third')!, 'getBoundingClientRect', {
      configurable: true,
      value: () => ({
        x: 0, y: 2_000, top: 2_000, left: 0, right: 100, bottom: 2_030,
        width: 100, height: 30, toJSON: () => ({}),
      }),
    });

    const { snapshot } = capturePage(document);
    expect(snapshot.elements.filter((item) => item.kind === 'search-result').map((item) => item.text))
      .toEqual(['第一条结果', '第二条结果', '第三条屏幕外结果']);
  });

  it('preserves separate accessible metadata for compact icon and pagination controls', () => {
    document.body.innerHTML = `
      <button aria-label="搜索" title="开始搜索"><svg></svg></button>
      <a aria-label="Page 3" title="第三页" href="https://example.com/search?start=20">3</a>
    `;

    const { snapshot } = capturePage(document);
    expect(snapshot.elements[0]).toMatchObject({ ariaLabel: '搜索', title: '开始搜索' });
    expect(snapshot.elements[1]).toMatchObject({ ariaLabel: 'Page 3', title: '第三页' });
  });

  it('preserves standard pagination relationship metadata', () => {
    document.body.innerHTML = '<a href="/next" rel="next" aria-label="Next">Next</a>';

    const { snapshot } = capturePage(document);

    expect(snapshot.elements[0]).toMatchObject({ rel: 'next', ariaLabel: 'Next' });
  });

  it('extracts semantic article paragraphs without bylines ads or recommendations', () => {
    document.title = '秋季养生指南 - 示例网站';
    document.body.innerHTML = `
      <nav>首页 健康 财经</nav>
      <main>
        <article>
          <h1>秋季养生指南</h1>
          <div class="byline"><p>作者：测试作者</p></div>
          <p>秋季天气逐渐转凉，日常作息应当规律，并根据气温变化及时增减衣物。</p>
          <div class="advertisement"><p>广告：立即购买保健产品</p></div>
          <p>饮食宜清淡多样，保证蔬菜、水果、蛋白质和充足饮水。</p>
          <aside><p>推荐阅读：冬季进补指南</p></aside>
          <p>适量运动和充足睡眠有助于保持稳定状态。</p>
          <footer>版权声明：未经许可不得转载</footer>
        </article>
      </main>
    `;

    const article = captureArticle(document);

    expect(article).toMatchObject({ title: '秋季养生指南', source: 'article', paragraphCount: 3 });
    expect(article.text).toContain('秋季天气逐渐转凉');
    expect(article.text).toContain('\n\n饮食宜清淡多样');
    expect(article.text).not.toMatch(/作者|广告|推荐阅读|版权声明/u);
  });

  it('bounds article extraction independently from normal page snapshots', () => {
    document.body.innerHTML = `<main><h1>测试文章</h1>${Array.from(
      { length: 400 },
      (_, index) => `<p>第${index}段${'正文内容'.repeat(30)}</p>`,
    ).join('')}</main>`;

    const article = captureArticle(document);
    const page = capturePage(document).snapshot;

    expect(article.text.length).toBeLessThanOrEqual(32_000);
    expect(article.paragraphCount).toBeGreaterThan(1);
    expect(page.visibleText.length).toBeLessThanOrEqual(16_000);
  });

  it('prefers a real article over a larger surrounding list container', () => {
    const cards = Array.from({ length: 8 }, (_, index) => `
      <section><h2>列表标题${index}</h2><p>列表摘要${index}${'内容'.repeat(18)}</p></section>
    `).join('');
    document.body.innerHTML = `
      <main>${cards}
        <article>
          <h1>具体文章</h1>
          <p>这是具体文章第一段，提供足够完整的正文信息用于可靠提取。</p>
          <p>这是具体文章第二段，继续补充内容并避免被首页列表覆盖。</p>
        </article>
      </main>
    `;

    const article = captureArticle(document);

    expect(article.source).toBe('article');
    expect(article.title).toBe('具体文章');
    expect(article.text).not.toContain('列表标题');
  });
});
