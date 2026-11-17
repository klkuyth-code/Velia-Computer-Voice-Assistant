const englishKeyword = /(?:hey|hi|hello)(?:velia|veliya|vellia|belia|valia|vella)/u;
const chineseKeyword = /(?:嘿|嗨|你好)(?:velia|veliya|维利亚|维莉娅|薇莉娅|维丽娅|维里亚|威利亚|微利亚)/u;

export function containsVeliaWakeWord(transcript: string): boolean {
  const normalized = transcript
    .normalize('NFKC')
    .toLowerCase()
    .replace(/[^\p{Letter}\p{Number}]+/gu, '');
  return englishKeyword.test(normalized) || chineseKeyword.test(normalized);
}
