// 最小 TVBox JS Spider 冒烟源：转发暴风资源 MacCMS 接口，验证引擎全链路
// 方法名按 TVBox JS 协议：home/category/detail/search/play

var API = 'https://bfzyapi.com/api.php/provide/vod/';

function home(filter) {
    var html = req(API + '?ac=list').content;
    var j = JSON.parse(html);
    var classes = (j.class || []).map(function (c) {
        return { type_id: String(c.type_id), type_name: c.type_name };
    });
    return JSON.stringify({ class: classes });
}

function category(tid, pg, filter, extend) {
    var html = req(API + '?ac=detail&t=' + tid + '&pg=' + pg).content;
    var j = JSON.parse(html);
    var list = (j.list || []).map(function (v) {
        return {
            vod_id: String(v.vod_id),
            vod_name: v.vod_name,
            vod_pic: v.vod_pic,
            vod_remarks: v.vod_remarks || ''
        };
    });
    return JSON.stringify({ list: list, page: parseInt(pg) });
}

function detail(id) {
    var html = req(API + '?ac=detail&ids=' + id).content;
    var j = JSON.parse(html);
    var v = (j.list || [])[0] || {};
    return JSON.stringify({
        list: [{
            vod_id: String(v.vod_id || id),
            vod_name: v.vod_name,
            vod_pic: v.vod_pic,
            type_name: v.type_name || '',
            vod_year: v.vod_year || '',
            vod_area: v.vod_area || '',
            vod_actor: v.vod_actor || '',
            vod_director: v.vod_director || '',
            vod_content: v.vod_content || '',
            vod_play_from: v.vod_play_from || '测试线路',
            vod_play_url: v.vod_play_url || ''
        }]
    });
}

function search(key, quick) {
    var html = req(API + '?ac=detail&wd=' + encodeURIComponent(key)).content;
    var j = JSON.parse(html);
    var list = (j.list || []).map(function (v) {
        return { vod_id: String(v.vod_id), vod_name: v.vod_name, vod_pic: v.vod_pic, vod_remarks: v.vod_remarks || '' };
    });
    return JSON.stringify({ list: list });
}

function play(flag, id, vipFlags) {
    return JSON.stringify({ parse: 0, playUrl: '', url: id, header: { 'User-Agent': 'Lavf/66.0' } });
}

export default {
    home: home,
    category: category,
    detail: detail,
    search: search,
    play: play
};
