#include <linux/miscdevice.h>
#include <linux/module.h>
#include <linux/dma-map-ops.h>

#define CMA_ALLOC _IOWR('Z', 0, u32)

static unsigned long cma_page_count = 0;
static struct page *cma_page_base = NULL;
static struct page **cma_page_ptrs = NULL;

static void cma_free(void)
{
  if(cma_page_ptrs)
  {
    kfree(cma_page_ptrs);
    cma_page_ptrs = NULL;
  }

  if(cma_page_base)
  {
    dma_release_from_contiguous(NULL, cma_page_base, cma_page_count);
    cma_page_base = NULL;
  }
}

static long cma_ioctl(struct file *file, unsigned int cmd, unsigned long arg)
{
  int i;
  u32 buffer;

  if(cmd != CMA_ALLOC) return -ENOTTY;

  if(copy_from_user(&buffer, (void __user *)arg, sizeof(buffer))) return -EFAULT;

  cma_free();

  cma_page_count = PAGE_ALIGN(buffer) >> PAGE_SHIFT;

  cma_page_ptrs = kmalloc_array(cma_page_count, sizeof(struct page *), GFP_KERNEL);

  if(!cma_page_ptrs) return -ENOMEM;

  cma_page_base = dma_alloc_from_contiguous(NULL, cma_page_count, 0, false);

  if(!cma_page_base)
  {
    cma_free();
    return -ENOMEM;
  }

  for(i = 0; i < cma_page_count; ++i) cma_page_ptrs[i] = &cma_page_base[i];

  buffer = page_to_phys(cma_page_base);

  if(copy_to_user((void __user *)arg, &buffer, sizeof(buffer))) return -EFAULT;

  return 0;
}

static int cma_mmap(struct file *file, struct vm_area_struct *vma)
{
  if(!cma_page_ptrs) return -ENXIO;
  vm_flags_set(vma, VM_MIXEDMAP);
  return vm_map_pages(vma, cma_page_ptrs, cma_page_count);
}

static int cma_release(struct inode *inode, struct file *file)
{
  cma_free();
  return 0;
}

static struct file_operations cma_fops =
{
  .unlocked_ioctl = cma_ioctl,
  .mmap = cma_mmap,
  .release = cma_release
};

struct miscdevice cma_device =
{
  .minor = MISC_DYNAMIC_MINOR,
  .name = "cma",
  .fops = &cma_fops
};

static int __init cma_init(void)
{
  return misc_register(&cma_device);
}

static void __exit cma_exit(void)
{
  cma_free();
  misc_deregister(&cma_device);
}

module_init(cma_init);
module_exit(cma_exit);
MODULE_LICENSE("MIT");
