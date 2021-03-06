//xfail:NOT_ALL_VERIFIED
//--local_size=1024 --num_groups=1024 --no-inline
//kernel.cl:[\s]+error:[\s]+possible[\s]+write-write[\s]+race on p\[[\d]+]
//Write by work item[\s]+[\d]+[\s]+with local id[\s]+[\d]+[\s]+in work group[\s]+[\d]+.+kernel.cl:14:[\d]+:[\s]+p\[get_local_id\(0\) \+ 1] = get_global_id\(0\);
//Write by work item[\s]+[\d]+[\s]+with local id[\s]+[\d]+[\s]+in work group[\s]+[\d]+.+kernel.cl:10:[\d]+:[\s]+p\[get_local_id\(0\)] = get_global_id\(0\);


__kernel void foo(__local int* p) {

  p[get_local_id(0)] = get_global_id(0);

  barrier(CLK_GLOBAL_MEM_FENCE);

  p[get_local_id(0) + 1] = get_global_id(0);
}
