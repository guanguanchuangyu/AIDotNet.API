import { del, get, post, postJson } from "../uitls/fetch";


const prefix = '/api/v1/user';

export const getUsers = (page: number, pageSize: number, keyword: string) => {
    return get(prefix + `?page=${page}&pageSize=${pageSize}&keyword=${keyword}`);
}

export const Remove = (id: string) => {
    return del(prefix + `/${id}`);
}

/**
 * 创建用户
 * @param user 
 * @returns 
 */
export const create = (user: any) => {
    return postJson(prefix, user);
}

export const enable = (id: string) => {
    return post(prefix + `/enable/${id}`);
}

/**
 * 获取当前用户信息
 */
export const info = () => {
    return get(prefix + `/info`);
}